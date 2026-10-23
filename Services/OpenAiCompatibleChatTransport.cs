using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;

namespace Jarvis.Services;

/// <summary>Provider-neutral transport for configured OpenAI-compatible fallback endpoints.</summary>
public static class OpenAiCompatibleChatTransport
{
    public static string SafeEndpointLabel(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "configured endpoint";
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string endpoint, string? apiKey,
        string payloadJson, CancellationToken cancellationToken, bool streaming = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiModelCatalogService.ResolveChatCompletionsUri(endpoint))
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        return await client.SendAsync(request,
            streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
    }
}
