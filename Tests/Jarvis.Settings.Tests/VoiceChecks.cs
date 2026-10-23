using System.Net;
using System.Net.Http;
using Jarvis.Services;

internal static class VoiceChecks
{
    public static async Task RunAsync()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested(); calls++;
            if (request.RequestUri!.Host != "api.elevenlabs.io" || !request.Headers.Contains("xi-api-key")) throw new Exception("Invalid authentication contract");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0,0,1,0]) };
        }));
        using var service = new ElevenLabsTtsService(client);
        var pcm = await service.GenerateAsync("Synthetic test", "testVoice", 1, "synthetic-key", default);
        if (pcm.Length != 4 || calls != 1) throw new Exception("PCM contract");
        Console.WriteLine("PASS: Mock ElevenLabs HTTPS authentication and PCM response");
        try { await service.GenerateAsync("test", "../bad", 1, "synthetic-key", default); throw new Exception("Accepted path"); }
        catch (InvalidOperationException) { }
        if (calls != 1) throw new Exception("Invalid voice made network request");
        Console.WriteLine("PASS: Invalid voice rejected before request");
        using var failureClient = new HttpClient(new Handler((_,_) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private backend body") }));
        using var failing = new ElevenLabsTtsService(failureClient);
        try { await failing.GenerateAsync("test", "testVoice", 1, "synthetic-key", default); throw new Exception("Accepted 429"); }
        catch (InvalidOperationException ex) { if (ex.Message.Contains("private") || ex.Message.Contains("synthetic-key")) throw new Exception("Leaked secret"); }
        Console.WriteLine("PASS: Quota error without response or key disclosure");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.GenerateAsync("test", "testVoice", 1, "synthetic-key", cancellation.Token); throw new Exception("Ignored cancellation"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: Cloud voice cancellation");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request, token));
    }
}
