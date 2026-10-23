using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jarvis.Services;

public sealed record ApiDiscoveredModel(string Id, string DisplayName);

public enum ModelCatalogFailure
{
    InvalidEndpoint,
    Authentication,
    Forbidden,
    NotFound,
    HttpError,
    Timeout,
    Network,
    InvalidResponse
}

public sealed class ModelCatalogException(ModelCatalogFailure failure, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ModelCatalogFailure Failure { get; } = failure;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Discovers actual chat-capable model IDs from Gemini and OpenAI-compatible APIs.</summary>
public sealed class ApiModelCatalogService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ApiModelCatalogService(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    public static bool IsGoogleGeminiEndpoint(Uri endpoint) =>
        endpoint.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Length > 256 || value.Any(char.IsWhiteSpace) || value.Contains("://", StringComparison.Ordinal)) return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return false;
        return !value.StartsWith('/') && !value.StartsWith('\\');
    }

    public static object CreateChatCompletionPayload(string modelId, object messages, object tools, object toolChoice, double temperature)
    {
        if (!IsValidModelId(modelId)) throw new ArgumentException("A valid model ID is required.", nameof(modelId));
        return new { model = modelId.Trim(), messages, tools, tool_choice = toolChoice, temperature };
    }

    public static Uri ResolveOpenAiModelsUri(string endpoint)
    {
        var baseUri = ValidateEndpoint(endpoint);
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/chat/completions".Length];
        if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) path += "/models";
        return WithPath(baseUri, path);
    }

    public static Uri ResolveChatCompletionsUri(string endpoint)
    {
        var baseUri = ValidateEndpoint(endpoint);
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (IsGoogleGeminiEndpoint(baseUri))
        {
            var version = GetGoogleApiVersion(path);
            return WithPath(baseUri, $"/{version}/openai/chat/completions");
        }

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return WithPath(baseUri, path);
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) path = path[..^"/models".Length];
        return WithPath(baseUri, path + "/chat/completions");
    }

    public async Task<IReadOnlyList<ApiDiscoveredModel>> ListModelsAsync(string endpoint, string? apiKey, CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateEndpoint(endpoint);
        if (IsGoogleGeminiEndpoint(baseUri)) return await ListGeminiModelsAsync(baseUri, apiKey, cancellationToken).ConfigureAwait(false);
        return await ListOpenAiModelsAsync(baseUri, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ApiDiscoveredModel>> ListGeminiModelsAsync(Uri baseUri, string? apiKey, CancellationToken cancellationToken)
    {
        var version = GetGoogleApiVersion(baseUri.AbsolutePath);
        var items = new List<ApiDiscoveredModel>();
        string? pageToken = null;
        for (var page = 0; page < 20; page++)
        {
            var path = $"/{version}/models";
            var uri = WithPath(baseUri, path, pageToken is null ? null : "pageToken=" + Uri.EscapeDataString(pageToken));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new ModelCatalogException(ModelCatalogFailure.InvalidResponse, "Gemini's model-list response did not contain a models array.");

            foreach (var item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array) continue;
                if (!methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String && method.GetString()!.Equals("generateContent", StringComparison.OrdinalIgnoreCase))) continue;
                var id = nameElement.GetString()?.Trim() ?? "";
                if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];
                if (!IsValidModelId(id)) continue;
                var display = item.TryGetProperty("displayName", out var displayElement) && displayElement.ValueKind == JsonValueKind.String
                    ? displayElement.GetString()?.Trim() : null;
                items.Add(new ApiDiscoveredModel(id, string.IsNullOrWhiteSpace(display) ? id : display));
            }

            pageToken = document.RootElement.TryGetProperty("nextPageToken", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString() : null;
            if (string.IsNullOrWhiteSpace(pageToken)) break;
        }

        return items.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<ApiDiscoveredModel>> ListOpenAiModelsAsync(Uri baseUri, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveOpenAiModelsUri(baseUri.AbsoluteUri));
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ModelCatalogException(ModelCatalogFailure.InvalidResponse, "The OpenAI-compatible model-list response did not contain a data array.");

        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("id").GetString()?.Trim() ?? "")
            .Where(IsValidModelId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ApiDiscoveredModel(id, id))
            .ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelCatalogException(ModelCatalogFailure.Timeout, "The model-list request timed out.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCatalogException(ModelCatalogFailure.Network, "The model service could not be reached. Check the URL and network connection.", inner: ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        var failure = status switch
        {
            HttpStatusCode.Unauthorized => ModelCatalogFailure.Authentication,
            HttpStatusCode.Forbidden => ModelCatalogFailure.Forbidden,
            HttpStatusCode.NotFound => ModelCatalogFailure.NotFound,
            _ => ModelCatalogFailure.HttpError
        };
        throw new ModelCatalogException(failure, $"The model service returned HTTP {(int)status}.", status);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ModelCatalogException(ModelCatalogFailure.InvalidResponse, "The model service returned malformed JSON.", inner: ex);
            }
        }
    }

    private static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ModelCatalogException(ModelCatalogFailure.InvalidEndpoint, "Enter a valid HTTP or HTTPS API base URL.");
        return uri;
    }

    private static string GetGoogleApiVersion(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var version = segments.FirstOrDefault(segment => segment.Equals("v1beta", StringComparison.OrdinalIgnoreCase) || segment.Equals("v1", StringComparison.OrdinalIgnoreCase));
        return version ?? "v1beta";
    }

    private static Uri WithPath(Uri original, string path, string? query = null)
    {
        var builder = new UriBuilder(original) { Path = path, Query = query ?? "", Fragment = "" };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
