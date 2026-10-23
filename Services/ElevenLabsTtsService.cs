using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

/// <summary>Explicit cloud TTS; fixed HTTPS host, no redirects, no retries and no response-body logging.</summary>
public sealed class ElevenLabsTtsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public ElevenLabsTtsService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("ElevenLabs: API-Schlüssel fehlt.");
        var request = new HttpRequestMessage(method, "https://api.elevenlabs.io/v1/" + path);
        request.Headers.Add("xi-api-key", apiKey);
        return request;
    }
    public async Task<byte[]> GenerateAsync(string text, string voiceId, double speed, string apiKey, CancellationToken token)
    {
        if (!Regex.IsMatch(voiceId ?? "", "^[A-Za-z0-9_-]{1,128}$"))
            throw new InvalidOperationException("ElevenLabs: Ungültige Stimmen-ID.");
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
            if (output.Length + count > 12_000_000) throw new InvalidDataException("ElevenLabs: Audioantwort ist zu groß.");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0 || output.Length % 2 != 0) throw new InvalidDataException("ElevenLabs: Ungültige PCM-Audiodaten.");
        return output.ToArray();
    }
    public async Task<IReadOnlyList<CloudVoice>> GetVoicesAsync(string apiKey, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, "voices", apiKey);
        using var response = await _http.SendAsync(request, token);
        EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return json.RootElement.GetProperty("voices").EnumerateArray()
            .Select(v => new CloudVoice(v.GetProperty("voice_id").GetString()!, v.GetProperty("name").GetString()!)).ToArray();
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException((int)response.StatusCode switch {
            401 or 403 => "ElevenLabs: Schlüssel oder Berechtigung ungültig.",
            429 => "ElevenLabs: Raten- oder Nutzungslimit erreicht.",
            _ => "ElevenLabs: Dienst derzeit nicht verfügbar."
        });
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
public sealed record CloudVoice(string Id, string Name);
