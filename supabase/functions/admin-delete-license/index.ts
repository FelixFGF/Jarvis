import { bearer, body, handleError, json, preflight, requiredString, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id);
    await api.rateLimit(user.id, "admin-delete-license", 20, 3600);

    const input = await body(req);
    const licenseId = requiredString(input.license_id, 36, "license ID");
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(licenseId)) {
      return json(400, { ok: false, error: "Invalid license ID." });
    }

    return json(200, await api.rpc("beta_admin_delete_license", {
      p_admin: user.id,
      p_license_id: licenseId,
    }));
  } catch (error) {
    return handleError(error);
  }
});
