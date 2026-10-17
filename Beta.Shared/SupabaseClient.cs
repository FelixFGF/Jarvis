using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;

namespace Jarvis.Beta;

public sealed class SupabaseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    public bool IsConfigured => _options.IsConfigured;
    public SupabaseClient(SupabaseOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<(BetaSession? Session, string? Error)> SignInAsync(string email, string password, CancellationToken ct = default)
        => await AuthAsync("/auth/v1/token?grant_type=password", new { email, password }, ct);

    public async Task<(BetaSession? Session, string? Error)> SignUpAsync(string email, string password, CancellationToken ct = default)
        => await AuthAsync("/auth/v1/signup", new { email, password }, ct);

    public async Task<(BetaSession? Session, string? Error)> RefreshAsync(string refreshToken, CancellationToken ct = default)
        => await AuthAsync("/auth/v1/token?grant_type=refresh_token", new { refresh_token = refreshToken }, ct);

    /// <summary>Runs Discord OAuth in the system browser and receives Supabase's PKCE callback on loopback.</summary>
    public async Task<(BetaSession? Session, DiscordProfile? Profile, string? Error)> SignInWithDiscordAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var callback = new Uri($"http://127.0.0.1:{port}/oauth/callback/");
        var state = OAuthCallbackSecurity.CreateRandomState();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectTo = Uri.EscapeDataString(callback + "?jarvis_state=" + Uri.EscapeDataString(state));
        var authUrl = new Uri(new Uri(_options.Url.TrimEnd('/') + "/"), $"auth/v1/authorize?provider=discord&scopes=identify&redirect_to={redirectTo}&code_challenge={challenge}&code_challenge_method=s256");
        Process.Start(new ProcessStartInfo(authUrl.AbsoluteUri) { UseShellExecute = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(3));
        string? authCode;
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(linked.Token);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var first = await reader.ReadLineAsync(linked.Token);
            if (string.IsNullOrWhiteSpace(first) || !first.StartsWith("GET /oauth/callback/?", StringComparison.Ordinal))
                return (null, null, "Discord sign-in returned an invalid callback.");
            var target = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(linked.Token))) { }
            var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + target).Query);
            if (!OAuthCallbackSecurity.IsValidState(state, query["jarvis_state"])) return (null, null, "Discord sign-in state validation failed.");
            authCode = query["code"];
            var html = Encoding.UTF8.GetBytes("<!doctype html><title>Jarvis</title><p>Sign-in complete. You can return to Jarvis.</p>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: {html.Length}\r\n\r\n");
            await stream.WriteAsync(header, linked.Token); await stream.WriteAsync(html, linked.Token);
        }
        catch (OperationCanceledException) { return (null, null, "Discord sign-in timed out or was cancelled."); }
        catch (Exception ex) when (ex is SocketException or IOException or UriFormatException) { return (null, null, "Could not complete the Discord sign-in callback."); }
        if (string.IsNullOrWhiteSpace(authCode)) return (null, null, "Discord sign-in was cancelled or rejected.");

        using var request = CreateRequest(HttpMethod.Post, "/auth/v1/token?grant_type=pkce", null);
        request.Content = JsonContent.Create(new { auth_code = authCode, code_verifier = verifier });
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (null, null, ReadSafeError(raw));
        using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || !root.TryGetProperty("refresh_token", out var refresh)) return (null, null, "The sign-in session was not returned.");
        var user = root.GetProperty("user");
        var expires = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds) ? seconds : 3600;
        var email = user.TryGetProperty("email", out var emailValue) ? emailValue.GetString() : null;
        var session = new BetaSession(access.GetString()!, refresh.GetString()!, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expires)), user.GetProperty("id").GetString()!, email);
        DiscordProfile? profile = null;
        if (root.TryGetProperty("provider_token", out var providerToken) && providerToken.ValueKind == JsonValueKind.String)
        {
            using var discordRequest = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
            discordRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerToken.GetString());
            using var discordResponse = await _http.SendAsync(discordRequest, ct);
            if (discordResponse.IsSuccessStatusCode)
            {
                using var discordDoc = JsonDocument.Parse(await discordResponse.Content.ReadAsStringAsync(ct)); var discord = discordDoc.RootElement;
                var id = discord.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9]{17,20}$"))
                {
                    var username = discord.TryGetProperty("username", out var usernameValue) ? usernameValue.GetString() : null;
                    var global = discord.TryGetProperty("global_name", out var globalValue) && globalValue.ValueKind == JsonValueKind.String ? globalValue.GetString() : null;
                    var avatar = discord.TryGetProperty("avatar", out var avatarValue) && avatarValue.ValueKind == JsonValueKind.String ? avatarValue.GetString() : null;
                    var avatarUrl = avatar is not null && System.Text.RegularExpressions.Regex.IsMatch(avatar, "^[A-Za-z0-9_]{2,128}$") ? $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=256" : null;
                    profile = new DiscordProfile(id, global ?? username, username, avatarUrl, "verified", global);
                }
            }
        }
        profile ??= await GetDiscordProfileFromSupabaseAsync(session, ct);
        return profile is null ? (session, null, "Discord authorized the sign-in, but its verified profile could not be loaded.") : (session with { DiscordProfile = profile }, profile, null);
    }

    private async Task<DiscordProfile?> GetDiscordProfileFromSupabaseAsync(BetaSession session, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "/auth/v1/user", session.AccessToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("identities", out var identities) || identities.ValueKind != JsonValueKind.Array) return null;
        foreach (var identity in identities.EnumerateArray())
        {
            if (!identity.TryGetProperty("provider", out var provider) || provider.GetString() != "discord") continue;
            var data = identity.TryGetProperty("identity_data", out var identityData) && identityData.ValueKind == JsonValueKind.Object ? identityData : default;
            var id = identity.TryGetProperty("provider_id", out var providerId) ? providerId.GetString() : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
            if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9]{17,20}$")) return null;
            string? ReadName(string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var username = ReadName("username");
            var global = ReadName("global_name") ?? ReadName("full_name");
            var avatarHash = ReadName("avatar");
            var avatarUrl = avatarHash is not null && System.Text.RegularExpressions.Regex.IsMatch(avatarHash, "^[A-Za-z0-9_]{2,128}$") ? $"https://cdn.discordapp.com/avatars/{id}/{avatarHash}.png?size=256" : null;
            return new DiscordProfile(id, global ?? username, username, avatarUrl, "verified", global);
        }
        return null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<(BetaSession? Session, string? Error)> AuthAsync(string route, object body, CancellationToken ct)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Post, route, null);
        request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = ReadSafeError(raw);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                return (null, message);
            throw new SupabaseRequestException((int)response.StatusCode, message);
        }
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || !root.TryGetProperty("refresh_token", out var refresh)) return (null, "Email confirmation may be required. Sign in after confirming your email.");
        if (string.IsNullOrWhiteSpace(access.GetString()) || string.IsNullOrWhiteSpace(refresh.GetString())) return (null, "Email confirmation may be required. Confirm your email, then sign in.");
        var user = root.GetProperty("user");
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;
        var email = user.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : null;
        return (new BetaSession(access.GetString()!, refresh.GetString()!, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)), user.GetProperty("id").GetString()!, email), null);
    }

    public async Task<T> InvokeAsync<T>(string function, BetaSession session, object? body = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var request = CreateRequest(body is null ? HttpMethod.Get : HttpMethod.Post, $"/functions/v1/{Uri.EscapeDataString(function)}", session.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new SupabaseRequestException((int)response.StatusCode, ReadSafeError(raw));
        return JsonSerializer.Deserialize<T>(raw, _json) ?? throw new InvalidDataException("The server returned an empty response.");
    }

    public async Task<T> InvokeAdminAsync<T>(string function, BetaSession session, object? body = null, CancellationToken ct = default)
        => await InvokeAsync<T>(function, session, body, ct);

    public async Task<bool> CheckConnectivityAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Get, "/auth/v1/health", null);
        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task SignOutAsync(BetaSession session, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Post, "/auth/v1/logout", session.AccessToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
            throw new SupabaseRequestException((int)response.StatusCode, ReadSafeError(await response.Content.ReadAsStringAsync(ct)));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_options.Url.TrimEnd('/') + "/"), path.TrimStart('/')));
        request.Headers.Add("apikey", _options.PublishableKey);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured) throw new InvalidOperationException("Supabase is not configured. Provide an HTTPS project URL and publishable/anon key.");
    }

    private string ReadSafeError(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var name in new[] { "msg", "message", "error_description", "error" })
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var message = value.GetString() ?? "Request failed";
                    return message.Length > 240 ? message[..240] : message;
                }
        }
        catch (JsonException) { }
        return "The backend rejected the request.";
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SupabaseRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class OAuthCallbackSecurity
{
    public static string CreateRandomState() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool IsValidState(string expected, string? actual)
    {
        if (actual is null) return false;
        var left = Encoding.UTF8.GetBytes(expected); var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public sealed record ApiResult(bool Ok, string? Error = null, string? Message = null,
    [property: JsonPropertyName("diagnostic_id")] string? DiagnosticId = null);
public sealed record InviteCreated(string Code, [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt, [property: JsonPropertyName("invite_id")] string InviteId);
public sealed record AdminLicense(
    [property: JsonPropertyName("license_id")] string LicenseId,
    [property: JsonPropertyName("user_id")] string UserId,
    string? Email,
    string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("invite_created_at")] DateTimeOffset? InviteCreatedAt = null,
    [property: JsonPropertyName("invite_redeemed_at")] DateTimeOffset? InviteRedeemedAt = null,
    [property: JsonPropertyName("discord_user_id")] string? DiscordUserId = null,
    [property: JsonPropertyName("discord_display_name")] string? DiscordDisplayName = null,
    [property: JsonPropertyName("discord_username")] string? DiscordUsername = null,
    [property: JsonPropertyName("discord_avatar_url")] string? DiscordAvatarUrl = null,
    [property: JsonPropertyName("discord_identity_status")] string? DiscordIdentityStatus = null,
    string? Note = null,
    [property: JsonPropertyName("discord_global_name")] string? DiscordGlobalName = null);
public sealed record AdminLicenseList([property: JsonPropertyName("licenses")] IReadOnlyList<AdminLicense> Licenses);
public sealed record AdminInvite(
    [property: JsonPropertyName("invite_id")] string InviteId,
    string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("redeemed_at")] DateTimeOffset? RedeemedAt,
    [property: JsonPropertyName("discord_user_id")] string? DiscordUserId,
    [property: JsonPropertyName("discord_display_name")] string? DiscordDisplayName,
    [property: JsonPropertyName("discord_global_name")] string? DiscordGlobalName,
    [property: JsonPropertyName("discord_username")] string? DiscordUsername,
    [property: JsonPropertyName("discord_identity_status")] string? DiscordIdentityStatus,
    string? Note,
    [property: JsonPropertyName("code_available")] bool CodeAvailable = false,
    [property: JsonPropertyName("license_id")] string? LicenseId = null,
    [property: JsonPropertyName("license_status")] string? LicenseStatus = null,
    [property: JsonPropertyName("redeemed_by")] string? RedeemedBy = null);
public sealed record AdminInviteList([property: JsonPropertyName("invitations")] IReadOnlyList<AdminInvite> Invitations);
public sealed record AdminAuditEvent([property: JsonPropertyName("event_id")] string EventId, [property: JsonPropertyName("admin_user_id")] string AdminUserId, string Action, [property: JsonPropertyName("target_id")] string TargetId, string Outcome, [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt, string? Metadata);
public sealed record AdminAuditList([property: JsonPropertyName("events")] IReadOnlyList<AdminAuditEvent> Events);
public sealed record LicenseValidation(bool Valid, string? Status, [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt, string? Message, [property: JsonPropertyName("discord_profile")] DiscordProfile? DiscordProfile = null);
public sealed record DiscordProfile([property: JsonPropertyName("discord_user_id")] string? DiscordUserId, [property: JsonPropertyName("discord_display_name")] string? DiscordDisplayName, [property: JsonPropertyName("discord_username")] string? DiscordUsername, [property: JsonPropertyName("discord_avatar_url")] string? DiscordAvatarUrl, [property: JsonPropertyName("discord_identity_status")] string? IdentityStatus, [property: JsonPropertyName("discord_global_name")] string? GlobalName = null);
