using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

public sealed record WebImageResult(string Title, string ThumbnailUrl, string SourceUrl, string Author, string License, string Provider = "", string FullImageUrl = "");
public enum ImageSearchOutcome { Results, Empty, HttpError, TimedOut, Unreachable, ParseFailed, Cancelled }
public sealed record ImageSearchAttempt(string Provider, ImageSearchOutcome Outcome, int? HttpStatus, int RawCount, int ParsedCount, int RetainedCount, string Detail);
public sealed record ImageSearchResponse(string Query, ImageSearchOutcome Outcome, IReadOnlyList<WebImageResult> Results, IReadOnlyList<ImageSearchAttempt> Attempts)
{
    public bool IsSuccess => Outcome == ImageSearchOutcome.Results;
}

public sealed class ImageSearchUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Keyless image search using Openverse with Wikimedia Commons as an independent fallback.</summary>
public sealed class ImageSearchService(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    private readonly bool _ownsClient = client is null;

    public async Task<IReadOnlyList<WebImageResult>> SearchAsync(string query, int limit, CancellationToken token)
    {
        var response = await SearchDetailedAsync(query, limit, token).ConfigureAwait(false);
        if (response.Outcome is ImageSearchOutcome.Results or ImageSearchOutcome.Empty) return response.Results;
        throw new ImageSearchUnavailableException("No image-search provider could be reached or parsed.");
    }

    public async Task<ImageSearchResponse> SearchDetailedAsync(string query, int limit, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200 || query.Any(char.IsControl)) throw new ArgumentException("Image query must contain 1–200 printable characters.", nameof(query));
        var count = Math.Clamp(limit, 1, 12);
        var attempts = new List<ImageSearchAttempt>();
        var collected = new List<WebImageResult>();
        var providers = new List<(string Name, Func<string, int, CancellationToken, Task<(IReadOnlyList<WebImageResult> Results, int Raw, int Parsed)>> Search)>();
        if (Regex.IsMatch(query, @"\bshader", RegexOptions.IgnoreCase)) providers.Add(("Modrinth", SearchModrinthAsync));
        if (Regex.IsMatch(query, @"\b(?:official|logo)", RegexOptions.IgnoreCase))
        {
            providers.Add(("Wikimedia Commons", SearchWikimediaAsync));
            providers.Add(("Openverse", SearchOpenverseAsync));
        }
        else
        {
            providers.Add(("Openverse", SearchOpenverseAsync));
            providers.Add(("Wikimedia Commons", SearchWikimediaAsync));
        }
        foreach (var provider in providers)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var run = await provider.Search(query, count, token).ConfigureAwait(false);
                attempts.Add(new(provider.Name, run.Results.Count > 0 ? ImageSearchOutcome.Results : ImageSearchOutcome.Empty, 200, run.Raw, run.Parsed, run.Results.Count,
                    run.Results.Count > 0 ? "Usable attributed images found." : "The provider returned no usable relevant images."));
                collected.AddRange(run.Results);
                collected = Rank(query, Distinct(collected, count * 2), count).ToList();
                if (collected.Count >= count) return new(query, ImageSearchOutcome.Results, collected, attempts);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                attempts.Add(new(provider.Name, ImageSearchOutcome.Cancelled, null, 0, 0, 0, "Cancelled by the caller.")); throw;
            }
            catch (TaskCanceledException) { attempts.Add(new(provider.Name, ImageSearchOutcome.TimedOut, null, 0, 0, 0, "The provider exceeded its time limit.")); }
            catch (HttpRequestException ex)
            {
                attempts.Add(new(provider.Name, ex.StatusCode is null ? ImageSearchOutcome.Unreachable : ImageSearchOutcome.HttpError,
                    ex.StatusCode is null ? null : (int)ex.StatusCode, 0, 0, 0, ex.StatusCode is null ? "The provider could not be reached." : $"HTTP {(int)ex.StatusCode}."));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                attempts.Add(new(provider.Name, ImageSearchOutcome.ParseFailed, null, 0, 0, 0, "The provider response did not match the expected schema."));
            }
        }
        if (collected.Count > 0) return new(query, ImageSearchOutcome.Results, Rank(query, collected, count), attempts);
        var validEmpty = attempts.Any(attempt => attempt.Outcome == ImageSearchOutcome.Empty);
        return new(query, validEmpty ? ImageSearchOutcome.Empty : attempts.LastOrDefault()?.Outcome ?? ImageSearchOutcome.Unreachable, [], attempts);
    }

    private async Task<(IReadOnlyList<WebImageResult> Results, int Raw, int Parsed)> SearchModrinthAsync(string query, int count, CancellationToken token)
    {
        var providerQuery = Regex.Replace(query, @"\b(minecraft|screenshots?|images?|pictures?|photos?)\b", " ", RegexOptions.IgnoreCase);
        providerQuery = Regex.Replace(providerQuery, @"\s+", " ").Trim();
        if (providerQuery.Length == 0) providerQuery = query;
        var facets = "[[\"project_type:shader\"]]";
        var uri = new Uri("https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(providerQuery) + "&limit=" + Math.Min(10, count) + "&facets=" + Uri.EscapeDataString(facets));
        using var json = await GetJsonAsync(uri, token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Modrinth returned an unexpected response schema.");
        var results = new List<WebImageResult>();
        foreach (var hit in hits.EnumerateArray())
        {
            var title = Clean(ReadString(hit, "title") ?? "Minecraft shader");
            var slug = ReadString(hit, "slug"); var projectType = ReadString(hit, "project_type") ?? "shader";
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var source = $"https://modrinth.com/{Uri.EscapeDataString(projectType)}/{Uri.EscapeDataString(slug)}";
            var author = Clean(ReadString(hit, "author") ?? ""); var license = Clean(ReadString(hit, "license") ?? "");
            if (hit.TryGetProperty("gallery", out var gallery) && gallery.ValueKind == JsonValueKind.Array)
            {
                foreach (var image in gallery.EnumerateArray().Where(node => node.ValueKind == JsonValueKind.String))
                {
                    var url = image.GetString(); if (!SafePublicHttps(url)) continue;
                    results.Add(new(title, url!, source, author, license, "Modrinth", url!));
                    if (results.Count >= count) break;
                }
            }
            if (results.Count >= count) break;
        }
        return (Distinct(results.Where(result => IsRelevant(query, result)), count), hits.GetArrayLength(), results.Count);
    }

    private async Task<(IReadOnlyList<WebImageResult> Results, int Raw, int Parsed)> SearchOpenverseAsync(string query, int count, CancellationToken token)
    {
        var uri = new Uri("https://api.openverse.org/v1/images/?q=" + Uri.EscapeDataString(query) + "&page_size=" + count + "&mature=false");
        using var json = await GetJsonAsync(uri, token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Openverse returned an unexpected response schema.");
        var raw = items.GetArrayLength(); var results = new List<WebImageResult>();
        foreach (var item in items.EnumerateArray())
        {
            var thumbnail = ReadString(item, "thumbnail");
            var fullImage = ReadString(item, "url") ?? thumbnail;
            var source = ReadString(item, "foreign_landing_url");
            if (!SafePublicHttps(thumbnail) || !SafePublicHttps(fullImage) || !SafePublicHttps(source)) continue;
            var license = ReadString(item, "license") ?? "";
            var version = ReadString(item, "license_version");
            if (!string.IsNullOrWhiteSpace(version)) license = $"{license} {version}".Trim();
            results.Add(new WebImageResult(Clean(ReadString(item, "title") ?? "Image"), thumbnail!, source!, Clean(ReadString(item, "creator") ?? ""), Clean(license), "Openverse", fullImage!));
        }
        return (Distinct(results.Where(result => IsRelevant(query, result)), count), raw, results.Count);
    }

    private async Task<(IReadOnlyList<WebImageResult> Results, int Raw, int Parsed)> SearchWikimediaAsync(string query, int count, CancellationToken token)
    {
        var uri = new Uri("https://commons.wikimedia.org/w/api.php?action=query&generator=search&gsrnamespace=6&gsrsearch="
            + Uri.EscapeDataString(query) + "&gsrlimit=" + count
            + "&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth=640&format=json&formatversion=2&origin=*");
        using var json = await GetJsonAsync(uri, token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("query", out var queryNode)) return ([], 0, 0);
        if (!queryNode.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Wikimedia Commons returned an unexpected response schema.");
        var raw = pages.GetArrayLength(); var results = new List<WebImageResult>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("imageinfo", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            var fullImage = ReadString(info, "url");
            var thumb = ReadString(info, "thumburl") ?? fullImage;
            var description = ReadString(info, "descriptionurl");
            if (!SafeHttpsHost(thumb, "upload.wikimedia.org") || !SafeHttpsHost(fullImage, "upload.wikimedia.org") || !SafeHttpsHost(description, "commons.wikimedia.org")) continue;
            var metadata = info.TryGetProperty("extmetadata", out var ext) ? ext : default;
            results.Add(new WebImageResult(Clean(ReadString(page, "title")?.Replace("File:", "", StringComparison.OrdinalIgnoreCase) ?? "Image"), thumb!, description!, Metadata(metadata, "Artist"), Metadata(metadata, "LicenseShortName"), "Wikimedia Commons", fullImage!));
        }
        return (Distinct(results.Where(result => IsRelevant(query, result)), count), raw, results.Count);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jarvis", "1.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    private static IReadOnlyList<WebImageResult> Distinct(IEnumerable<WebImageResult> results, int count) => results
        .GroupBy(item => string.IsNullOrWhiteSpace(item.FullImageUrl) ? item.ThumbnailUrl : item.FullImageUrl, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).Take(count).ToArray();
    internal static IReadOnlyList<WebImageResult> Rank(string query, IEnumerable<WebImageResult> results, int count)
    {
        var logo = Regex.IsMatch(query, @"\blogo", RegexOptions.IgnoreCase);
        var shader = Regex.IsMatch(query, @"\bshader", RegexOptions.IgnoreCase);
        int Score(WebImageResult item)
        {
            var evidence = (item.Title + " " + item.SourceUrl + " " + item.Provider).ToLowerInvariant();
            var score = item.Provider switch { "Modrinth" => shader ? 45 : 8, "Wikimedia Commons" => logo ? 35 : 16, "Openverse" => 12, _ => 0 };
            if (logo && evidence.Contains("logo", StringComparison.Ordinal)) score += 45;
            if (shader && evidence.Contains("shader", StringComparison.Ordinal)) score += 35;
            if (query.Contains("official", StringComparison.OrdinalIgnoreCase) && (evidence.Contains("official", StringComparison.Ordinal) || item.Provider == "Wikimedia Commons")) score += 20;
            return score;
        }
        return results.Where(item => IsRelevant(query, item)).OrderByDescending(Score).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).Take(count).ToArray();
    }
    internal static bool IsRelevant(string query, WebImageResult result)
    {
        static string Normalize(string value) => Regex.Replace(WebUtility.HtmlDecode(value).ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ");
        var ignored = new HashSet<string>(["official", "image", "images", "picture", "pictures", "photo", "photos", "screenshot", "screenshots", "bild", "bilder", "foto", "fotos"], StringComparer.Ordinal);
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(term => term.Length > 2 && !ignored.Contains(term)).Distinct().ToArray();
        if (terms.Length == 0) return true;
        var evidence = Normalize(result.Title + " " + result.SourceUrl + " " + result.Provider);
        static string Stem(string value) => value.EndsWith("s", StringComparison.Ordinal) && value.Length > 4 ? value[..^1] : value;
        var matches = terms.Count(term => evidence.Contains(Stem(term), StringComparison.Ordinal));
        var distinctive = terms.Where(term => !term.Equals("minecraft", StringComparison.Ordinal)).ToArray();
        var hasDistinctiveMatch = distinctive.Length == 0 || distinctive.Any(term => evidence.Contains(Stem(term), StringComparison.Ordinal));
        if (Regex.IsMatch(query, @"\blogo", RegexOptions.IgnoreCase) && !evidence.Contains("logo", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(query, @"\bshader", RegexOptions.IgnoreCase) && !evidence.Contains("shader", StringComparison.Ordinal) && result.Provider != "Modrinth") return false;
        return hasDistinctiveMatch && matches >= Math.Max(1, (int)Math.Ceiling(terms.Length * .5));
    }
    private static string? ReadString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static string Metadata(JsonElement metadata, string name) => metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var node) && node.TryGetProperty("value", out var value) ? Clean(value.GetString() ?? "") : "";
    private static string Clean(string text) => WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " ")).Trim();
    private static bool SafeHttpsHost(string? value, string host) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    private static bool SafePublicHttps(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(uri.Host, out var address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !(bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31));
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
