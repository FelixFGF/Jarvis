using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

public enum ElevenLabsErrorKind { MissingKey, InvalidVoice, Authentication, InsufficientAccess, Quota, Connectivity, Timeout, Server, Parsing, NoVoices, Unavailable, InvalidAudio }
public sealed class ElevenLabsException(ElevenLabsErrorKind kind, string message, Exception? inner = null) : Exception(message, inner) { public ElevenLabsErrorKind Kind { get; } = kind; }

/// <summary>Explicit cloud TTS; fixed HTTPS host, no redirects, no retries and no response-body logging.</summary>
public sealed class ElevenLabsTtsService : IDisposable
{
    public static readonly Uri OfficialApiKeysUri = new("https://elevenlabs.io/app/settings/api-keys");
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public ElevenLabsTtsService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string apiKey, int apiVersion = 1)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ElevenLabsException(ElevenLabsErrorKind.MissingKey, "ElevenLabs API key is missing.");
        var request = new HttpRequestMessage(method, $"https://api.elevenlabs.io/v{apiVersion}/" + path);
        request.Headers.Add("xi-api-key", apiKey);
        return request;
    }
    public async Task<byte[]> GenerateAsync(string text, string voiceId, double speed, string apiKey, CancellationToken token)
    {
        if (!Regex.IsMatch(voiceId ?? "", "^[A-Za-z0-9_-]{1,128}$"))
            throw new ElevenLabsException(ElevenLabsErrorKind.InvalidVoice, "ElevenLabs voice ID is invalid.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) throw new ArgumentException("Speech chunk must contain 1–2000 characters.");
        using var request = Request(HttpMethod.Post, $"text-to-speech/{voiceId}/stream?output_format=pcm_24000", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new {
            text, model_id = "eleven_multilingual_v2", voice_settings = new { speed = Math.Clamp(speed, 0.7, 1.2) }
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 12_000_000) throw new ElevenLabsException(ElevenLabsErrorKind.InvalidAudio, "ElevenLabs audio response is too large.");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0 || output.Length % 2 != 0) throw new ElevenLabsException(ElevenLabsErrorKind.InvalidAudio, "ElevenLabs returned invalid PCM audio.");
        return output.ToArray();
    }
    public async Task<IReadOnlyList<CloudVoice>> GetVoicesAsync(string apiKey, CancellationToken token)
    {
        try
        {
            var collected = new List<CloudVoice>(); string? nextPageToken = null;
            for (var page = 0; page < 5; page++)
            {
                var path = "voices?page_size=100&sort=name&sort_direction=asc" + (string.IsNullOrWhiteSpace(nextPageToken) ? "" : "&next_page_token=" + Uri.EscapeDataString(nextPageToken));
                using var request = Request(HttpMethod.Get, path, apiKey, 2);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                EnsureSuccess(response);
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                if (!json.RootElement.TryGetProperty("voices", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new ElevenLabsException(ElevenLabsErrorKind.Parsing, "ElevenLabs returned an unexpected voice-list schema.");
                collected.AddRange(items.EnumerateArray().Select(ParseVoice).Where(voice => voice is not null).Cast<CloudVoice>());
                var hasMore = json.RootElement.TryGetProperty("has_more", out var hasMoreNode) && hasMoreNode.ValueKind == JsonValueKind.True;
                nextPageToken = json.RootElement.TryGetProperty("next_page_token", out var tokenNode) && tokenNode.ValueKind == JsonValueKind.String ? tokenNode.GetString() : null;
                if (!hasMore || string.IsNullOrWhiteSpace(nextPageToken)) break;
            }
            var voices = collected.GroupBy(voice => voice.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (voices.Length == 0) throw new ElevenLabsException(ElevenLabsErrorKind.NoVoices, "ElevenLabs returned no usable voices for this account.");
            return voices;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw new ElevenLabsException(ElevenLabsErrorKind.Timeout, "The ElevenLabs voice request timed out.", ex); }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { throw new ElevenLabsException(ElevenLabsErrorKind.Connectivity, "ElevenLabs could not be reached.", ex); }
        catch (JsonException ex) { throw new ElevenLabsException(ElevenLabsErrorKind.Parsing, "ElevenLabs returned malformed JSON.", ex); }
    }
    private static CloudVoice? ParseVoice(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("voice_id", out var idNode) || idNode.ValueKind != JsonValueKind.String) return null;
        var id = idNode.GetString()?.Trim();
        var name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String ? nameNode.GetString()?.Trim() : null;
        return string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}$") ? null : new CloudVoice(id, string.IsNullOrWhiteSpace(name) ? id : name!);
    }
    public static string? ResolvePreferredVoiceId(IEnumerable<CloudVoice> voices, string? preferredId) =>
        voices.FirstOrDefault(voice => voice.Id.Equals(preferredId?.Trim(), StringComparison.Ordinal))?.Id;
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var kind = (int)response.StatusCode switch { 401 => ElevenLabsErrorKind.Authentication, 403 => ElevenLabsErrorKind.InsufficientAccess, 429 => ElevenLabsErrorKind.Quota, >= 500 => ElevenLabsErrorKind.Server, _ => ElevenLabsErrorKind.Unavailable };
        var message = kind switch { ElevenLabsErrorKind.Authentication => "ElevenLabs rejected the API key or account permission.", ElevenLabsErrorKind.Quota => "ElevenLabs rate or usage limit was reached.", _ => "ElevenLabs is currently unavailable." };
        throw new ElevenLabsException(kind, message, new HttpRequestException(message, null, response.StatusCode));
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
public sealed record CloudVoice(string Id, string Name);
