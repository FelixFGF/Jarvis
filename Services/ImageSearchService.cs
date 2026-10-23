using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;

namespace Jarvis.Services;

public sealed record WebImageResult(string Title, string ThumbnailUrl, string SourceUrl, string Author, string License);

/// <summary>Keyless image search backed by Wikimedia Commons' public API.</summary>
public sealed class ImageSearchService(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private readonly bool _ownsClient = client is null;

    public async Task<IReadOnlyList<WebImageResult>> SearchAsync(string query, int limit, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200 || query.Any(char.IsControl)) throw new ArgumentException("Image query must contain 1–200 printable characters.", nameof(query));
        var count = Math.Clamp(limit, 1, 8);
        var uri = new Uri("https://commons.wikimedia.org/w/api.php?action=query&generator=search&gsrnamespace=6&gsrsearch="
            + Uri.EscapeDataString(query) + "&gsrlimit=" + count
            + "&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth=480&format=json&formatversion=2&origin=*");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jarvis", "1.1"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("query", out var queryNode) || !queryNode.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return [];
        var results = new List<WebImageResult>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("imageinfo", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            var thumb = ReadString(info, "thumburl") ?? ReadString(info, "url");
            var description = ReadString(info, "descriptionurl");
            if (!SafeHttps(thumb, "upload.wikimedia.org") || !SafeHttps(description, "commons.wikimedia.org")) continue;
            var metadata = info.TryGetProperty("extmetadata", out var ext) ? ext : default;
            results.Add(new WebImageResult(
                Clean(ReadString(page, "title")?.Replace("File:", "", StringComparison.OrdinalIgnoreCase) ?? "Image"),
                thumb!, description!, Metadata(metadata, "Artist"), Metadata(metadata, "LicenseShortName")));
        }
        return results;
    }

    private static string? ReadString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static string Metadata(JsonElement metadata, string name) => metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var node) && node.TryGetProperty("value", out var value) ? Clean(value.GetString() ?? "") : "";
    private static string Clean(string text) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ")).Trim();
    private static bool SafeHttps(string? value, string host) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
