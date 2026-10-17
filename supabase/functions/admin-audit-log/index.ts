import { bearer, body, handleError, json, preflight, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id);
    await api.rateLimit(user.id, "admin-audit-log", 120, 60);
    const input = await body(req);
    const limit = Number(input.limit ?? 100);
    if (!Number.isInteger(limit) || limit < 1 || limit > 250) return json(400, { ok: false, error: "Limit must be between 1 and 250." });
    return json(200, await api.rpc("beta_admin_audit", { p_admin: user.id, p_limit: limit }));
  } catch (error) {
    return handleError(error);
  }
});
