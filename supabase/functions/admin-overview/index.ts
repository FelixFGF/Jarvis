import { bearer, handleError, json, preflight, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id);
    return json(200, await api.rpc("beta_admin_overview", { p_admin: user.id }));
  } catch (error) {
    return handleError(error);
  }
});
