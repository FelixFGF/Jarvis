import { bearer, body, handleError, json, preflight, requiredString, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id);
    await api.rateLimit(user.id, "admin-reinstate-license", 60, 60);
    const input = await body(req);
    const licenseId = requiredString(input.license_id, 36, "license ID");
    if (!/^[0-9a-f-]{36}$/i.test(licenseId)) return json(400, { ok: false, error: "Invalid license ID." });
    return json(200, await api.rpc("beta_admin_change_license", { p_admin: user.id, p_license_id: licenseId, p_action: "reinstate" }));
  } catch (error) {
    return handleError(error);
  }
});
