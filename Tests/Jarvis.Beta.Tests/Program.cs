using Jarvis.Beta;
using System.Net;
using System.Text;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Beta session protection tests require Windows DPAPI.");

var root = Path.Combine(Path.GetTempPath(), "jarvis-beta-tests-" + Guid.NewGuid().ToString("N"));
var validationCalls = 0;
var failLicenseDelete = false;
var forbiddenLicenseDelete = false;
var missingLicenseDelete = false;
Directory.CreateDirectory(root);
try
{
    Check(new SupabaseOptions("https://unit-test.supabase.co", "sb_publishable_test-key").IsConfigured, "HTTPS Supabase URL with publishable key accepted");
    Check(new SupabaseOptions("http://127.0.0.1:54321", "sb_publishable_local-key").IsConfigured, "local Supabase HTTP with publishable key accepted");
    Check(!new SupabaseOptions("https://unit-test.supabase.co", "sb_secret_test-key").IsConfigured, "Supabase secret keys are rejected");
    Check(!new SupabaseOptions("https://unit-test.supabase.co", "").IsConfigured, "missing client key is rejected");
    Check(!new SupabaseOptions("http://example.com", "key").IsConfigured, "non-local HTTP Supabase URL rejected");
    var oauthState = OAuthCallbackSecurity.CreateRandomState();
    Check(OAuthCallbackSecurity.IsValidState(oauthState, oauthState) && !OAuthCallbackSecurity.IsValidState(oauthState, "attacker-state") && !OAuthCallbackSecurity.IsValidState(oauthState, null), "OAuth callback state accepts only exact matching state");

    using var client = new SupabaseClient(new SupabaseOptions("https://unit-test.supabase.co", "sb_publishable_test-key"), new Handler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
        {
            Check(request.Headers.TryGetValues("apikey", out var keys) && keys.Single() == "sb_publishable_test-key", "only the publishable key is used for Auth");
            var requestText = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(requestText.Contains("password-not-persisted", StringComparison.Ordinal), "password is transmitted only in the Auth request");
            return Json("""{"access_token":"access-token-test","refresh_token":"refresh-token-test","expires_in":3600,"user":{"id":"11111111-1111-4111-8111-111111111111","email":"tester@example.invalid"}}""");
        }
        if (request.RequestUri.AbsolutePath == "/auth/v1/logout")
        {
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "sign-out revokes the current authenticated Supabase session");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (request.RequestUri.AbsolutePath == "/functions/v1/validate-license")
        {
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "Edge call includes authenticated user's access token");
            var validation = Interlocked.Increment(ref validationCalls);
            return validation switch
            {
                1 => Json("""{"valid":true,"status":"active","expires_at":null,"discord_profile":{"discord_user_id":"123456789012345678","discord_display_name":"Felix Beta","discord_username":null,"discord_avatar_url":null,"discord_identity_status":"verified"}}"""),
                2 => Json("""{"valid":false,"status":"revoked","expires_at":null,"message":"License revoked"}"""),
                _ => Json("""{"valid":false,"status":"expired","expires_at":"2026-01-01T00:00:00Z","message":"License expired"}""")
            };
        }
        if (request.RequestUri.AbsolutePath == "/functions/v1/redeem-invite")
        {
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "invitation redemption uses the authenticated Supabase session");
            var requestText = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(requestText.Contains("VALID-CODE-123456", StringComparison.Ordinal), "redemption sends the supplied invitation code");
            Check(!requestText.Contains("discord_user_id", StringComparison.OrdinalIgnoreCase), "redemption never accepts a client-supplied Discord identity");
            return Json("""{"ok":true,"message":"Invitation redeemed"}""");
        }
        if (request.RequestUri.AbsolutePath == "/functions/v1/admin-list-licenses")
        {
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "admin API uses the account token, not service role");
            return Json("""{"licenses":[{"license_id":"22222222-2222-4222-8222-222222222222","user_id":"11111111-1111-4111-8111-111111111111","email":"tester@example.invalid","status":"active","created_at":"2026-01-01T00:00:00Z","expires_at":null,"revoked_at":null,"discord_user_id":"123456789012345678","discord_display_name":"Felix Beta","discord_identity_status":"admin_entered","note":"ticket-123"}]}""");
        }
        if (request.RequestUri.AbsolutePath == "/functions/v1/admin-list-invites")
        {
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "invitation management uses the authenticated admin access token");
            return Json("""{"invitations":[{"invite_id":"33333333-3333-4333-8333-333333333333","status":"pending","created_at":"2026-10-16T10:00:00Z","expires_at":null,"redeemed_at":null,"discord_user_id":"123456789012345678","discord_display_name":"Felix Beta","discord_global_name":null,"discord_username":null,"discord_identity_status":"admin_entered","note":"ticket-123","code_available":false,"license_id":null,"license_status":null,"redeemed_by":null}]}""");
        }
        if (request.RequestUri.AbsolutePath == "/functions/v1/admin-delete-invite")
        {
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "invitation deletion uses the authenticated admin access token");
            var requestText = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(requestText.Contains("33333333-3333-4333-8333-333333333333", StringComparison.Ordinal), "invitation deletion sends the selected server invitation ID");
            return Json("""{"ok":true}""");
        }
        if (request.RequestUri.AbsolutePath == "/functions/v1/admin-delete-license")
        {
            Check(request.Method == HttpMethod.Post, "license deletion uses POST");
            Check(request.Headers.TryGetValues("apikey", out var apiKeys) && apiKeys.Single() == "sb_publishable_test-key", "license deletion uses the configured public Supabase key");
            Check(request.Headers.Authorization?.Parameter == "access-token-test", "license deletion uses the authenticated administrator access token");
            var requestText = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(requestText.Contains("62000000-0000-4000-8000-000000000001", StringComparison.Ordinal), "license deletion sends the selected license ID to the protected endpoint");
            if (forbiddenLicenseDelete)
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"Administrator access required.\"}", Encoding.UTF8, "application/json") };
            if (missingLicenseDelete)
                return Json("""{"ok":false,"message":"License not found."}""");
            return failLicenseDelete
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":\"Database operation failed.\"}", Encoding.UTF8, "application/json") }
                : Json("""{"ok":true,"license_id":"62000000-0000-4000-8000-000000000001"}""");
        }
        throw new InvalidOperationException("Unexpected test request: " + request.RequestUri);
    }));

    var auth = await client.SignInAsync("tester@example.invalid", "password-not-persisted");
    Check(auth.Session?.UserId == "11111111-1111-4111-8111-111111111111", "Supabase user identity parsed from Auth response");
    var session = auth.Session!;
    var license = await client.InvokeAsync<LicenseValidation>("validate-license", session, new { });
    Check(license.Valid && license.Status == "active" && license.DiscordProfile?.DiscordDisplayName == "Felix Beta" && license.DiscordProfile.IdentityStatus == "verified", "valid server license returns verified Discord profile for app identity");
    var redeemed = await client.InvokeAsync<ApiResult>("redeem-invite", session, new { code = "VALID-CODE-123456" });
    Check(redeemed.Ok, "valid invitation redemption is accepted only after authenticated server request");
    var revoked = await client.InvokeAsync<LicenseValidation>("validate-license", session, new { });
    Check(!revoked.Valid && revoked.Status == "revoked", "revoked server license is not treated as beta access");
    var expired = await client.InvokeAsync<LicenseValidation>("validate-license", session, new { });
    Check(!expired.Valid && expired.Status == "expired", "expired server license is not treated as beta access");
    await client.SignOutAsync(session);
    var licenses = await client.InvokeAdminAsync<AdminLicenseList>("admin-list-licenses", session, new { search = "" });
    Check(licenses.Licenses.Count == 1 && licenses.Licenses[0].LicenseId == "22222222-2222-4222-8222-222222222222" && licenses.Licenses[0].DiscordUserId == "123456789012345678" && licenses.Licenses[0].Email == "tester@example.invalid" && licenses.Licenses[0].Note == "ticket-123", "admin DTO keeps Supabase account separate from Discord identity and note");
    var invites = await client.InvokeAdminAsync<AdminInviteList>("admin-list-invites", session, new { status = "pending", search = "" });
    Check(invites.Invitations.Count == 1 && invites.Invitations[0].Status == "pending" && invites.Invitations[0].DiscordUserId == "123456789012345678" && !invites.Invitations[0].CodeAvailable && invites.Invitations[0].LicenseId is null, "admin invitation DTO contains metadata but never assumes raw code retrieval");
    var deletion = await client.InvokeAdminAsync<ApiResult>("admin-delete-invite", session, new { invite_id = invites.Invitations[0].InviteId });
    Check(deletion.Ok, "admin delete endpoint returns explicit server-confirmed success");
    var licenseDeletion = await client.InvokeAdminAsync<ApiResult>("admin-delete-license", session, new { license_id = "62000000-0000-4000-8000-000000000001" });
    Check(licenseDeletion.Ok && licenseDeletion.DiagnosticId is null, "admin license deletion accepts the server-confirmed success response");
    missingLicenseDelete = true;
    var missingLicense = await client.InvokeAdminAsync<ApiResult>("admin-delete-license", session, new { license_id = "62000000-0000-4000-8000-000000000001" });
    Check(!missingLicense.Ok && missingLicense.Message == "License not found.", "missing license response is explicit and cannot appear successful");
    missingLicenseDelete = false;
    forbiddenLicenseDelete = true;
    var unauthorizedDeleteReported = false;
    try { await client.InvokeAdminAsync<ApiResult>("admin-delete-license", session, new { license_id = "62000000-0000-4000-8000-000000000001" }); }
    catch (SupabaseRequestException ex) when (ex.StatusCode == (int)HttpStatusCode.Forbidden) { unauthorizedDeleteReported = true; }
    Check(unauthorizedDeleteReported, "unauthorized license deletion is reported as forbidden");
    forbiddenLicenseDelete = false;
    failLicenseDelete = true;
    var databaseFailureReported = false;
    try { await client.InvokeAdminAsync<ApiResult>("admin-delete-license", session, new { license_id = "62000000-0000-4000-8000-000000000001" }); }
    catch (SupabaseRequestException ex) when (ex.StatusCode == (int)HttpStatusCode.InternalServerError) { databaseFailureReported = true; }
    Check(databaseFailureReported, "admin license deletion reports backend/database failures instead of claiming success");

    var sessionPath = Path.Combine(root, "session.dat");
    var store = new BetaSessionStore(sessionPath);
    var profileSession = session with { DiscordProfile = new DiscordProfile("123456789012345678", "Felix", "felix", "https://cdn.discordapp.com/avatars/123456789012345678/hash.png?size=256", "verified", "Felix") };
    await store.SaveAsync(profileSession);
    var protectedBytes = await File.ReadAllBytesAsync(sessionPath);
    var clearText = Encoding.UTF8.GetString(protectedBytes);
    Check(!clearText.Contains("access-token-test", StringComparison.Ordinal) && !clearText.Contains("refresh-token-test", StringComparison.Ordinal), "session tokens are not stored as plaintext");
    var restored = await store.LoadAsync();
    Check(restored?.RefreshToken == "refresh-token-test" && restored.DiscordProfile?.DiscordUserId == "123456789012345678", "DPAPI session round-trip preserves OAuth profile while protecting tokens");
    await store.DeleteAsync();

    Console.WriteLine("PASS: endpoint validation, publishable-key Auth, bearer-token Edge requests, response DTOs, and DPAPI session storage.");
}
finally { Directory.Delete(root, recursive: true); }


void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
}
