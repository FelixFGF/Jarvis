import { bearer, body, handleError, json, preflight, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req); if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id); await api.rateLimit(user.id, "admin-list-invites", 120, 60);
    const input = await body(req);
    const status = typeof input.status === "string" ? input.status : "all";
    if (!new Set(["all", "pending", "redeemed", "revoked", "expired"]).has(status)) return json(400, { ok: false, error: "Invalid invitation status filter." });
    const search = typeof input.search === "string" ? input.search.trim().slice(0, 100) : "";
    const result = await api.rpc<{ invitations: unknown[] }>("beta_admin_list_invites", { p_admin: user.id, p_status: status, p_search: search });
    return json(200, result);
  } catch (error) { return handleError(error); }
});
