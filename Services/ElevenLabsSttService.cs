using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

/// <summary>
/// ElevenLabs batch speech-to-text client. Audio leaves the device only when the
/// user explicitly selects this cloud provider. The request uses the documented
/// multipart /v1/speech-to-text endpoint and never logs credentials or response bodies.
/// </summary>
public sealed class ElevenLabsSttService : IDisposable
{
    public const string DefaultModelId = "scribe_v2";
    public const long MaximumUploadBytes = 3L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ElevenLabsSttService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
    }

    public static bool IsValidModelId(string? modelId) =>
        Regex.IsMatch(modelId?.Trim() ?? "", @"^scribe_[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task<string> TranscribeAsync(string audioPath, string modelId, string apiKey, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ElevenLabsException(ElevenLabsErrorKind.MissingKey, "ElevenLabs API key is missing.");
        if (!IsValidModelId(modelId)) throw new ElevenLabsException(ElevenLabsErrorKind.Unavailable, "The ElevenLabs speech-to-text model ID is invalid. Use a supported Scribe model such as scribe_v2.");
        var file = new FileInfo(audioPath);
        if (!file.Exists) throw new FileNotFoundException("The recorded audio file is unavailable.", audioPath);
        if (file.Length == 0) throw new ElevenLabsException(ElevenLabsErrorKind.InvalidAudio, "The recorded audio file is empty.");
        if (file.Length > MaximumUploadBytes) throw new ElevenLabsException(ElevenLabsErrorKind.InvalidAudio, "The recording exceeds the ElevenLabs upload limit.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
            request.Headers.Add("xi-api-key", apiKey);
            await using var stream = file.OpenRead();
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(MediaType(audioPath));
            using var content = new MultipartFormDataContent();
            content.Add(fileContent, "file", file.Name);
            content.Add(new StringContent(modelId.Trim()), "model_id");
            request.Content = content;

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            EnsureSuccess(response);
            await using var responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("text", out var textNode) || textNode.ValueKind != JsonValueKind.String)
                throw new ElevenLabsException(ElevenLabsErrorKind.Parsing, "ElevenLabs returned an unexpected transcription schema.");
            var transcription = textNode.GetString()?.Trim() ?? "";
            if (transcription.Length == 0) throw new ElevenLabsException(ElevenLabsErrorKind.InvalidAudio, "ElevenLabs did not recognize understandable speech in this recording.");
            return transcription;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw new ElevenLabsException(ElevenLabsErrorKind.Timeout, "The ElevenLabs transcription request timed out.", ex); }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { throw new ElevenLabsException(ElevenLabsErrorKind.Connectivity, "ElevenLabs could not be reached.", ex); }
        catch (JsonException ex) { throw new ElevenLabsException(ElevenLabsErrorKind.Parsing, "ElevenLabs returned malformed transcription JSON.", ex); }
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/x-m4a", ".flac" => "audio/flac",
        ".ogg" => "audio/ogg", ".opus" => "audio/opus", ".webm" => "audio/webm", _ => "application/octet-stream"
    };

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var kind = (int)response.StatusCode switch
        {
            401 => ElevenLabsErrorKind.Authentication,
            403 => ElevenLabsErrorKind.InsufficientAccess,
            413 or 415 or 422 => ElevenLabsErrorKind.InvalidAudio,
            429 => ElevenLabsErrorKind.Quota,
            >= 500 => ElevenLabsErrorKind.Server,
            _ => ElevenLabsErrorKind.Unavailable
        };
        var message = kind switch
        {
            ElevenLabsErrorKind.Authentication => "ElevenLabs rejected the API key.",
            ElevenLabsErrorKind.InsufficientAccess => "The ElevenLabs key does not have speech-to-text access.",
            ElevenLabsErrorKind.Quota => "The ElevenLabs speech-to-text rate or usage limit was reached.",
            ElevenLabsErrorKind.InvalidAudio => "ElevenLabs rejected the audio file or transcription parameters.",
            ElevenLabsErrorKind.Server => "ElevenLabs speech-to-text is temporarily unavailable.",
            _ => "The ElevenLabs transcription request failed."
        };
        throw new ElevenLabsException(kind, message, new HttpRequestException(message, null, response.StatusCode));
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

public sealed class ElevenLabsSpeechRecognitionProvider(ElevenLabsSttService service) : ISpeechRecognitionProvider
{
    public string Id => SpeechRecognitionProviderRouter.ElevenLabs;

    public SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ElevenLabsApiKey)) return new(false, "An ElevenLabs API key is required.", []);
        if (!ElevenLabsSttService.IsValidModelId(request.ElevenLabsModelId)) return new(false, "Select a valid ElevenLabs Scribe speech-to-text model.", []);
        return new(true, "ElevenLabs cloud speech-to-text is ready. Recordings are uploaded to ElevenLabs when transcription starts.", ["automatic", "de", "en"]);
    }

    public Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var availability = GetAvailability(request);
        if (!availability.Available) throw new InvalidOperationException(availability.Message);
        return Task.CompletedTask;
    }

    public Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken) =>
        service.TranscribeAsync(request.AudioPath, request.ElevenLabsModelId ?? ElevenLabsSttService.DefaultModelId, request.ElevenLabsApiKey ?? "", cancellationToken);
}
