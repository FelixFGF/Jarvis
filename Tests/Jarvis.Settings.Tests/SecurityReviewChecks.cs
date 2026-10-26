using System.Net;
using System.Net.Http;
using System.Text;
using Jarvis.Services;

internal static class SecurityReviewChecks
{
    public static async Task RunAsync()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"decision\":\"approve\",\"risk\":\"low\",\"reason\":\"Read-only metadata\"}"}}]}""");
        using (var service = new SecurityReviewService(new HttpClient(handler)))
        {
            var decision = await service.ReviewAsync("https://review.example/v1", "reviewer", "secret", "Inspect", "window metadata", CancellationToken.None);
            Check(decision.Approved && decision.Risk == "low", "valid low-risk security review is accepted");
            Check(handler.LastAuthorization == "Bearer secret" && !handler.LastBody.Contains("secret", StringComparison.Ordinal), "reviewer credential is sent only in the authorization header");
        }
        using (var service = new SecurityReviewService(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}"))))
        {
            try { await service.ReviewAsync("https://review.example/v1", "reviewer", "", "Inspect", "metadata", CancellationToken.None); throw new Exception("malformed review accepted"); }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { Console.WriteLine("PASS: malformed security review fails closed"); }
        }
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS: " + name); }
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string LastAuthorization { get; private set; } = "";
        public string LastBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString() ?? "";
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
