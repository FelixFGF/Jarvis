import { bearer, body, handleError, hashInviteCode, json, optionalExpiry, preflight, randomInvite, requiredString, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req); if (early) return early;
  try {
    const api = new SupabaseAdmin(); const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id); await api.rateLimit(user.id, "admin-create-invite", 20, 3600);
    const input = await body(req);
    // Each code belongs to one intended member. Bulk code generation without
    // individual identities would make the invitation-to-member binding ambiguous.
    if (input.count !== undefined && input.count !== 1) return json(400, {ok:false,error:"Create one invitation per Discord member."});
    const discordUserId = requiredString(input.discord_user_id, 20, "Discord user ID");
    if (!/^[0-9]{17,20}$/.test(discordUserId)) return json(400, {ok:false,error:"Enter a valid Discord user ID (17–20 digits)."});
    const discordDisplayName = requiredString(input.discord_display_name, 100, "Discord display name");
    const note = input.note === undefined || input.note === null || input.note === "" ? null : requiredString(input.note, 500, "note");
    const expiresAt = optionalExpiry(input.expires_at);
    const code = randomInvite();
    // Redemption normalizes user-entered case/spacing, then restores this
    // canonical grouped form before hashing. Both paths must hash identical UTF-8.
    const hash = await hashInviteCode(code);
    const persisted = await api.rpc<{ok:boolean;reason?:string;invite_id?:string;expires_at?:string|null;discord_identity_status?:string}>("beta_create_invite_discord", {
      p_hash: `\\x${[...hash].map(b=>b.toString(16).padStart(2,"0")).join("")}`,
      p_expiry: expiresAt,
      p_note: note,
      p_discord_user_id: discordUserId,
      p_discord_display_name: discordDisplayName,
      p_admin: user.id,
    });
    if (!persisted.ok || !persisted.invite_id) {
      return json(409,{ok:false,error:persisted.reason === "discord_identity_exists" ? "An active or redeemed invitation already exists for this Discord ID." : "The invitation could not be created. Check for an existing Discord invitation and try again."});
    }
    // Raw code exists only in memory and is returned once after the DB transaction succeeds.
    return json(200,{ok:true,invitation:{code,invite_id:persisted.invite_id,expires_at:persisted.expires_at,discord_identity_status:persisted.discord_identity_status}});
  } catch (error) { return handleError(error); }
});
