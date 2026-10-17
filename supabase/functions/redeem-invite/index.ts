import { ApiError, bearer, body, handleError, hashInviteCode, json, normalizeInvite, preflight, requiredString, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req); if (early) return early;
  const attemptId = crypto.randomUUID();
  let userId: string | undefined;
  let stage = "authentication";
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    userId = user.id;
    if (!user.discord) {
      console.warn(JSON.stringify({ event: "invite_redemption_denied", attempt_id: attemptId, user_id: user.id, reason: "missing_discord_identity" }));
      return diagnosticJson(403, { ok: false, error: "Link a Discord account before redeeming a beta invitation.", diagnostic_id: attemptId }, attemptId);
    }
    stage = "rate_limit";
    await api.rateLimitClient(req, user.id, "redeem-invite", 8, 900);
    stage = "request_validation";
    const input = await body(req);
    const code = requiredString(input.code, 80, "invitation code");
    if (!/^JRV[A-Z0-9]{32}$/.test(normalizeInvite(code))) {
      console.warn(JSON.stringify({ event: "invite_redemption_denied", attempt_id: attemptId, user_id: user.id, reason: "invalid_code_format" }));
      return diagnosticJson(400, { ok: false, error: "Invitation could not be redeemed.", diagnostic_id: attemptId }, attemptId);
    }
    stage = "database_rpc";
    const result = await api.rpc<{ok:boolean;message?:string;reason?:string;license_id?:string}>("beta_redeem_invite_discord", {
      p_hash: `\\x${[...await hashInviteCode(code)].map(b=>b.toString(16).padStart(2,"0")).join("")}`,
      p_user_id: user.id,
      p_discord_user_id: user.discord.id,
      p_discord_username: user.discord.username,
      p_discord_global_name: user.discord.globalName,
      p_discord_avatar_hash: user.discord.avatar,
      p_attempt_id: attemptId,
    });
    if (!result.ok) {
      // Only the non-sensitive category is logged; never log raw codes, tokens or Discord IDs.
      console.warn(JSON.stringify({ event: "invite_redemption_denied", attempt_id: attemptId, user_id: user.id, reason: result.reason ?? "unspecified" }));
      return diagnosticJson(400, { ok: false, error: "Invitation could not be redeemed. Check that it is unused, unexpired, and assigned to the Discord account currently signed in.", diagnostic_id: attemptId }, attemptId);
    }
    return diagnosticJson(200, { ok: true, license_id: result.license_id }, attemptId);
  } catch (error) {
    console.error(JSON.stringify({ event: "invite_redemption_error", attempt_id: attemptId, user_id: userId ?? null, stage, error_type: error instanceof Error ? error.name : "unknown" }));
    const response = handleError(error);
    const payload = await response.json().catch(() => ({ ok: false, error: "The licensing service could not complete the request." }));
    return diagnosticJson(response.status, { ...payload, diagnostic_id: attemptId }, attemptId);
  }
});

function diagnosticJson(status: number, payload: unknown, attemptId: string): Response {
  const response = json(status, payload);
  const headers = new Headers(response.headers);
  headers.set("x-jarvis-diagnostic-id", attemptId);
  return new Response(response.body, { status: response.status, headers });
}
