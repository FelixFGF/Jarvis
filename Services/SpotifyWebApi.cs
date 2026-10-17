using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace Jarvis.Services;

/// <summary>Spotify OAuth PKCE and Web API operations for Jarvis' native Windows client.</summary>
public sealed class SpotifyWebApi
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback/";
    private const string Scopes = "user-read-private user-read-playback-state user-modify-playback-state user-read-currently-playing user-library-modify playlist-read-private playlist-read-collaborative playlist-modify-private playlist-modify-public";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<(string RefreshToken, string Account)> ConnectAsync(string clientId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 100) throw new InvalidDataException("Gib eine gültige Spotify Client ID ein.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        using var listener = new TcpListener(IPAddress.Loopback, 43821);
        listener.Start(1);
        try
        {
            var authorize = "https://accounts.spotify.com/authorize?" + Encode(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(), ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
                ["scope"] = Scopes, ["state"] = state, ["code_challenge_method"] = "S256", ["code_challenge"] = challenge
            });
            Process.Start(new ProcessStartInfo(authorize) { UseShellExecute = true });
            using var client = await listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(token) ?? throw new InvalidDataException("Spotify hat keinen OAuth-Rückruf geliefert.");
            string? line; while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token))) { }
            var uri = new Uri("http://127.0.0.1" + (requestLine.Split(' ').ElementAtOrDefault(1) ?? "/"));
            var query = ParseQuery(uri.Query);
            var html = query.ContainsKey("error") ? "<h2>Spotify connection declined</h2>You can close this window." : "<h2>Spotify connected</h2>You can close this window and return to Jarvis.";
            var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}");
            await stream.WriteAsync(bytes, token);
            if (query.TryGetValue("error", out var error)) throw new InvalidOperationException("Spotify authorization declined: " + error);
            if (!query.TryGetValue("state", out var returnedState) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(state))) throw new InvalidDataException("Spotify OAuth state mismatch.");
            if (!query.TryGetValue("code", out var code)) throw new InvalidDataException("Spotify callback did not contain an authorization code.");
            using var response = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = clientId.Trim(), ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier }), token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Spotify token endpoint returned {(int)response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            var refresh = json.RootElement.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() : null;
            var access = json.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("Spotify returned no access token.");
            if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidDataException("Spotify returned no refresh token. Revoke Jarvis in Spotify and connect again.");
            var me = await ApiAsync(access, HttpMethod.Get, "me", null, token);
            using var profile = JsonDocument.Parse(me);
            var account = profile.RootElement.TryGetProperty("display_name", out var name) ? name.GetString() ?? "Spotify" : "Spotify";
            return (refresh, account);
        }
        finally { listener.Stop(); }
    }

    public async Task<string> RefreshAsync(string clientId, string refreshToken, CancellationToken token)
    {
        using var response = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }), token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Spotify authorization expired or was rejected ({(int)response.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("Spotify returned no access token.");
    }

    public async Task<string> ApiAsync(string accessToken, HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/" + path.TrimStart('/'));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Spotify API returned {(int)response.StatusCode}: {content}");
        return content;
    }

    private static string Encode(Dictionary<string, string> values) => string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')), StringComparer.Ordinal);
}
