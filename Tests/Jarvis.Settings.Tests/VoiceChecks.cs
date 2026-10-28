using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using Jarvis.Services;

internal static class VoiceChecks
{
    public static async Task RunAsync()
    {
        var calls = 0; string? lastPreviewPath = null;
        using var client = new HttpClient(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested(); calls++;
            if (request.RequestUri!.Host != "api.elevenlabs.io" || !request.Headers.Contains("xi-api-key")) throw new Exception("Invalid authentication contract");
            lastPreviewPath = request.RequestUri.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0,0,1,0]) };
        }));
        using var service = new ElevenLabsTtsService(client);
        var pcm = await service.GenerateAsync("Synthetic test", "testVoice", 1, "synthetic-key", default);
        if (pcm.Length != 4 || calls != 1) throw new Exception("PCM contract");
        Console.WriteLine("PASS: Mock ElevenLabs HTTPS authentication and PCM response");
        _ = await service.GenerateAsync("Preview", "currently-selected-voice", 1, "synthetic-key", default);
        if (lastPreviewPath != "/v1/text-to-speech/currently-selected-voice/stream") throw new Exception("Voice preview did not use the currently selected voice ID");
        Console.WriteLine("PASS: Mock voice preview uses the current selected voice ID");
        try { await service.GenerateAsync("test", "../bad", 1, "synthetic-key", default); throw new Exception("Accepted path"); }
        catch (ElevenLabsException ex) when (ex.Kind == ElevenLabsErrorKind.InvalidVoice) { }
        if (calls != 2) throw new Exception("Invalid voice made network request");
        Console.WriteLine("PASS: Invalid voice rejected before request");
        using var failureClient = new HttpClient(new Handler((_,_) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private backend body") }));
        using var failing = new ElevenLabsTtsService(failureClient);
        try { await failing.GenerateAsync("test", "testVoice", 1, "synthetic-key", default); throw new Exception("Accepted 429"); }
        catch (ElevenLabsException ex) { if (ex.Kind != ElevenLabsErrorKind.Quota || ex.Message.Contains("private") || ex.Message.Contains("synthetic-key")) throw new Exception("Leaked secret or wrong error classification"); }
        Console.WriteLine("PASS: Quota error without response or key disclosure");
        if (ElevenLabsTtsService.OfficialApiKeysUri != new Uri("https://elevenlabs.io/app/settings/api-keys")) throw new Exception("Incorrect official ElevenLabs destination");
        Console.WriteLine("PASS: Official ElevenLabs API-key destination");
        using var voicesClient = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri != "https://api.elevenlabs.io/v2/voices?page_size=100&sort=name&sort_direction=asc" || request.Headers.GetValues("xi-api-key").Single() != "voice-key") throw new Exception("Incorrect voices endpoint or key header");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"voices\":[{\"voice_id\":\"voice-1\",\"name\":\"Test voice\"},{\"voice_id\":\"voice-2\",\"name\":\"Second voice\"}],\"has_more\":false}") };
        }));
        using var voicesService = new ElevenLabsTtsService(voicesClient);
        var voices = await voicesService.GetVoicesAsync("voice-key", default);
        if (voices.Count != 2 || !voices.Any(voice => voice.Id == "voice-1" && voice.Name == "Test voice") || !voices.Any(voice => voice.Id == "voice-2" && voice.Name == "Second voice")) throw new Exception("Voice connection test did not parse the verified account response");
        if (ElevenLabsTtsService.ResolvePreferredVoiceId(voices, "voice-2") != "voice-2" || ElevenLabsTtsService.ResolvePreferredVoiceId(voices, "removed-voice") is not null) throw new Exception("Voice selection was not preserved only when still available.");
        Console.WriteLine("PASS: ElevenLabs voice connection endpoint and response parsing");
        foreach (var test in new[]
        {
            (HttpStatusCode.Unauthorized, "{}", ElevenLabsErrorKind.Authentication),
            (HttpStatusCode.Forbidden, "{}", ElevenLabsErrorKind.InsufficientAccess),
            (HttpStatusCode.TooManyRequests, "{}", ElevenLabsErrorKind.Quota),
            (HttpStatusCode.InternalServerError, "{}", ElevenLabsErrorKind.Server),
            (HttpStatusCode.OK, "{not-json", ElevenLabsErrorKind.Parsing),
            (HttpStatusCode.OK, "{\"voices\":[]}", ElevenLabsErrorKind.NoVoices)
        })
        {
            using var errorClient = new HttpClient(new Handler((_, _) => new HttpResponseMessage(test.Item1) { Content = new StringContent(test.Item2) }));
            using var errorService = new ElevenLabsTtsService(errorClient);
            try { await errorService.GetVoicesAsync("voice-key", default); throw new Exception($"Accepted voice-list failure {test.Item3}"); }
            catch (ElevenLabsException ex) when (ex.Kind == test.Item3) { }
        }
        var pageCalls = 0;
        using (var pagingClient = new HttpClient(new Handler((request, _) =>
        {
            pageCalls++;
            var second = request.RequestUri!.Query.Contains("next_page_token=page-2", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(second
                ? "{\"voices\":[{\"voice_id\":\"voice-2\",\"name\":\"Beta\"}],\"has_more\":false}"
                : "{\"voices\":[{\"voice_id\":\"voice-1\",\"name\":\"Alpha\"}],\"has_more\":true,\"next_page_token\":\"page-2\"}") };
        })))
        using (var pagingService = new ElevenLabsTtsService(pagingClient))
        {
            var paged = await pagingService.GetVoicesAsync("voice-key", default);
            if (pageCalls != 2 || paged.Count != 2 || paged[1].Id != "voice-2") throw new Exception("ElevenLabs pagination did not retain voices from all pages.");
        }
        Console.WriteLine("PASS: ElevenLabs voice pagination and selection persistence");
        using (var timeoutClient = new HttpClient(new AsyncHandler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(2), token); return new HttpResponseMessage(HttpStatusCode.OK); })) { Timeout = TimeSpan.FromMilliseconds(20) })
        using (var timeoutService = new ElevenLabsTtsService(timeoutClient))
        {
            try { await timeoutService.GetVoicesAsync("voice-key", default); throw new Exception("Accepted timed-out voice request"); }
            catch (ElevenLabsException ex) when (ex.Kind == ElevenLabsErrorKind.Timeout) { }
        }
        Console.WriteLine("PASS: ElevenLabs empty, authentication, quota, server, timeout, and malformed-response classification");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.GenerateAsync("test", "testVoice", 1, "synthetic-key", cancellation.Token); throw new Exception("Ignored cancellation"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: Cloud voice cancellation");

        var cacheCalls = 0;
        {
            var cacheRoot = Path.Combine(AppContext.BaseDirectory, "jarvis-tts-cache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new SpeechAudioCache(cacheRoot);
                async Task<byte[]> Generate(CancellationToken token) { token.ThrowIfCancellationRequested(); cacheCalls++; await Task.Yield(); return [0, 0, 1, 0]; }
                _ = await cache.GetOrCreateAsync("voice|speed|Kann ich dir noch bei etwas helfen?", Generate, default);
                _ = await cache.GetOrCreateAsync("voice|speed|Kann ich dir noch bei etwas helfen?", Generate, default);
                if (cacheCalls != 1 || cache.SizeBytes() != 4) throw new Exception("Repeated ElevenLabs phrase was not served from the local PCM cache.");
                cache.Clear();
                if (cache.SizeBytes() != 0) throw new Exception("Speech cache could not be cleared.");
            }
            finally { try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true); } catch { } }
        }
        Console.WriteLine("PASS: Optional ElevenLabs phrase cache reuses exact audio and can be cleared");

        using (var groqClient = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != "https://api.groq.com/openai/v1/audio/transcriptions" || request.Headers.Authorization?.Scheme != "Bearer") throw new Exception("Groq request contract");
            var form = await request.Content!.ReadAsStringAsync(token); if (!form.Contains("whisper-large-v3-turbo") || !form.Contains("name=file")) throw new Exception("Groq multipart contract");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Hallo Jarvis\"}") };
        })))
        using (var groq = new GroqSpeechRecognitionProvider(groqClient))
        {
            var wav = Path.GetTempFileName(); await File.WriteAllBytesAsync(wav, [1,2,3,4]);
            try { var text = await groq.TranscribeAsync(new(wav, null, "de-DE", GroqApiKey:"secret", GroqModelId:GroqSpeechRecognitionProvider.DefaultModelId), default); if(text!="Hallo Jarvis")throw new Exception("Groq parse"); }
            finally { File.Delete(wav); }
        }
        Console.WriteLine("PASS: Groq official endpoint, bearer authentication, multipart audio and transcription parsing");

        using (var rsa = RSA.Create(2048))
        {
            var credentials = JsonSerializer.Serialize(new { client_email="jarvis-test@example.test", private_key=rsa.ExportRSAPrivateKeyPem(), token_uri="https://oauth2.googleapis.com/token" });
            var googleCalls=0;
            using var googleClient = new HttpClient(new AsyncHandler(async (request, token) =>
            {
                googleCalls++;
                if(request.RequestUri!.Host=="oauth2.googleapis.com") return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"test-token\"}")};
                if(request.RequestUri.AbsoluteUri!="https://speech.googleapis.com/v2/projects/project-one/locations/global/recognizers/_:recognize"||request.Headers.Authorization?.Parameter!="test-token")throw new Exception("Google request contract");
                var payload=await request.Content!.ReadAsStringAsync(token);if(!payload.Contains("autoDecodingConfig")||!payload.Contains("short"))throw new Exception("Google payload contract");
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"results\":[{\"alternatives\":[{\"transcript\":\"Hello Jarvis\"}]}]}")};
            }));
            using var google = new GoogleCloudSpeechRecognitionProvider(googleClient);var wav=Path.GetTempFileName();await File.WriteAllBytesAsync(wav,[1,2,3,4]);
            try{var text=await google.TranscribeAsync(new(wav,null,"en-US",GoogleCredentialsJson:credentials,GoogleProjectId:"project-one",GoogleModelId:"short"),default);if(text!="Hello Jarvis"||googleCalls!=2)throw new Exception("Google result contract");}finally{File.Delete(wav);}
        }
        Console.WriteLine("PASS: Google service-account JWT flow, v2 recognize endpoint and result parsing");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request, token));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
