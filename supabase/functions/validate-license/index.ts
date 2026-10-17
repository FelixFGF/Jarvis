import { bearer, handleError, json, preflight, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req); if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    if (!user.discord) return json(200, { valid: false, status: "discord_required", message: "A Discord account must be linked to this beta account." });
    await api.rateLimit(user.id, "validate-license", 120, 60);
    const result = await api.rpc<{valid:boolean;status:string;expires_at?:string;message?:string;discord_profile?:unknown}>("beta_validate_license_discord", { p_user_id: user.id, p_discord_user_id: user.discord.id });
    return json(200, result);
  } catch (error) { return handleError(error); }
});
