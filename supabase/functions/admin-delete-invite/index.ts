import { ApiError, bearer, body, handleError, json, preflight, requiredString, SupabaseAdmin } from "../_shared/http.ts";

Deno.serve(async (req) => {
  const early = preflight(req); if (early) return early;
  try {
    const api = new SupabaseAdmin();
    const user = await api.authenticatedUser(bearer(req));
    await api.requireAdmin(user.id);
    await api.rateLimit(user.id, "admin-delete-invite", 30, 3600);
    const input = await body(req);
    const inviteId = requiredString(input.invite_id, 36, "invitation ID");
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(inviteId)) throw new ApiError(400, "Invalid invitation ID.");
    const result = await api.rpc<{ ok: boolean; message?: string }>("beta_admin_delete_invite", { p_admin: user.id, p_invite_id: inviteId });
    if (!result.ok) return json(409, { ok: false, error: result.message ?? "Invitation could not be deleted." });
    return json(200, { ok: true });
  } catch (error) { return handleError(error); }
});
