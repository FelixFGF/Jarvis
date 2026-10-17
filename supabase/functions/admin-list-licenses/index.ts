import { bearer, body, handleError, json, preflight, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id);
    await api.rateLimit(user.id, "admin-list-licenses", 120, 60);
    const input = await body(req);
    const search = typeof input.search === "string" ? input.search.slice(0, 100) : "";
    return json(200, await api.rpc("beta_admin_list_licenses", { p_admin: user.id, p_search: search }));
  } catch (error) {
    return handleError(error);
  }
});
