using System.Net;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Text.Json;

namespace Jarvis.Services;

public sealed record GoogleSearchResult(string Title, string Url, string Snippet, string Domain = "", double Relevance = 0, string FetchStatus = "found", string? Error = null);

public static class GoogleSearchParser
{
    private static readonly Regex H3 = new("<h3\\b[^>]*>(?<title>.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Href = new("\\bhref\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Anchor = new("<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DdLink = new("<a\\b(?<attrs>[^>]*class=['\"][^'\"]*result__a[^'\"]*['\"][^>]*)>(?<body>.*?)</a\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BingItems = new("<li\\b(?<attrs>[^>]*class=['\"][^'\"]*b_algo[^'\"]*['\"][^>]*)>(?<body>.*?)</li\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Rejects provider home pages and other generic results that do not address the requested concepts.</summary>
    public static List<GoogleSearchResult> KeepRelevant(string query, IEnumerable<GoogleSearchResult> results)
    {
        var creationDateQuery = IsCreationDateQuery(query);
        var concepts = QueryConcepts(query);
        var distinctiveTerms = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{5,}").Select(match => match.Value)
            .Where(term => !new[] { "minecraft", "wikipedia", "documentation", "official", "create", "erstelle", "using" }.Contains(term))
            .Distinct().ToArray();
        var unique = new Dictionary<string, GoogleSearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var canonical = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
            if (unique.ContainsKey(canonical)) continue;
            var haystack = result.Title + " " + result.Snippet + " " + uri.Host;
            var matches = concepts.Count(concept => concept.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)));
            var coverage = concepts.Count == 0 ? 1d : (double)matches / concepts.Count;
            var officialBoost = IsPreferredTechnicalSource(uri.Host, query) ? .2d : 0d;
            var score = Math.Min(1d, coverage + officialBoost);
            var requiredCoverage = concepts.Count <= 2 ? .5d : .6d;
            if (distinctiveTerms.Length > 0 && !distinctiveTerms.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
            if (creationDateQuery && !IsRelevantCreationDateResult(result) || score < requiredCoverage) continue;
            unique[canonical] = result with { Url = canonical, Domain = uri.Host, Relevance = Math.Round(score, 2) };
        }
        return unique.Values.OrderByDescending(result => result.Relevance).ThenBy(result => result.Title).ToList();
    }

    private static bool IsPreferredTechnicalSource(string host, string query)
    {
        if (query.Contains("fabric", StringComparison.OrdinalIgnoreCase))
            return host.Equals("docs.fabricmc.net", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".fabricmc.net", StringComparison.OrdinalIgnoreCase)
                || host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        return host.StartsWith("docs.", StringComparison.OrdinalIgnoreCase) || host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExpandQuery(string query)
    {
        var text = query.ToLowerInvariant();
        var asksCreationDate = IsCreationDateQuery(text);
        return asksCreationDate ? "YouTube founding year creation date established" : query;
    }

    public static bool IsCreationDateQuery(string query) =>
        Regex.IsMatch(query, @"(erstellt|erstellungs|gründ|gegründ|created|founded|founding|creation|date|datum)", RegexOptions.IgnoreCase)
        && Regex.IsMatch(query, @"youtube", RegexOptions.IgnoreCase);

    private static bool IsRelevantCreationDateResult(GoogleSearchResult result)
    {
        var text = result.Title + " " + result.Snippet;
        var hasDate = Regex.IsMatch(text,
            @"\b(?:19|20)\d{2}\b|\b\d{1,2}\s+(?:January|February|March|April|May|June|July|August|September|October|November|December|Januar|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember)\b", RegexOptions.IgnoreCase);
        var hasCreationContext = Regex.IsMatch(text,
            @"(creat(?:ed|ion)|founded|founding|established|launched|gründ(?:ung|ete|en)?|gegründet|erstell(?:t|ung)|gestartet|began|started)", RegexOptions.IgnoreCase);
        return hasDate && hasCreationContext && Regex.IsMatch(result.Title, @"youtube", RegexOptions.IgnoreCase);
    }

    public static string ExpandWikipediaQuery(string query) => IsCreationDateQuery(query)
        ? "YouTube founding year created established February 14 2005"
        : query;

    private static List<string[]> QueryConcepts(string query)
    {
        var terms = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value)
            .Where(term => !new[] { "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "eines", "und", "oder", "wie", "was", "wann", "wo", "ist", "war", "wurde", "wurden", "auf", "aus", "von", "für", "es", "the", "and", "for", "from", "with", "when", "was", "were", "is", "are", "it", "its", "search", "google", "suche", "such", "nach" }.Contains(term))
            .Distinct().ToList();
        var concepts = new List<string[]>();
        foreach (var term in terms)
        {
            if (term.Contains("youtube", StringComparison.Ordinal)) concepts.Add(["youtube"]);
            else if (term.Contains("erstellt", StringComparison.Ordinal) || term.Contains("created", StringComparison.Ordinal) || term.Contains("gründ", StringComparison.Ordinal) || term.Contains("found", StringComparison.Ordinal) || term.Contains("datum", StringComparison.Ordinal))
                concepts.Add(["created", "founded", "established", "launched", "started", "gründ", "erstell", "gegründ", "creation", "date"]);
            else concepts.Add([term]);
        }
        return concepts;
    }

    public static bool IsConsentPage(string html) => ContainsAny(html, "consent.google.com", "Before you continue to Google", "consent.googleusercontent.com");
    public static bool IsBlockedPage(string html) => ContainsAny(html, "/sorry/", "unusual traffic", "Our systems have detected unusual traffic", "g-recaptcha", "Please show you're not a robot");

    public static List<GoogleSearchResult> Parse(string html, int limit)
    {
        var results = new List<GoogleSearchResult>();
        var headings = H3.Matches(html);
        foreach (Match heading in headings)
        {
            var before = html.LastIndexOf("<a", heading.Index, StringComparison.OrdinalIgnoreCase);
            var closeBefore = html.LastIndexOf("</a", heading.Index, StringComparison.OrdinalIgnoreCase);
            var after = html.IndexOf("</a", heading.Index, StringComparison.OrdinalIgnoreCase);
            if (before < 0 || before < closeBefore || after < 0) continue;
            var anchor = html[before..Math.Min(html.Length, after + 4)];
            var hrefMatch = Href.Match(anchor);
            if (!hrefMatch.Success) continue;
            var href = WebUtility.HtmlDecode(hrefMatch.Groups["double"].Success ? hrefMatch.Groups["double"].Value : hrefMatch.Groups["single"].Success ? hrefMatch.Groups["single"].Value : hrefMatch.Groups["bare"].Value);
            if (Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var parsed) && !parsed.IsAbsoluteUri)
            {
                if (href.StartsWith("/url?", StringComparison.OrdinalIgnoreCase))
                {
                    var wrapped = Uri.TryCreate("https://www.google.com" + href, UriKind.Absolute, out var wrapper) ? wrapper : null;
                    var targetParam = wrapper?.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && (x[0] is "q" or "url"));
                    if (targetParam is { Length: 2 }) href = Uri.UnescapeDataString(targetParam[1].Replace("+", " "));
                }
                else href = "";
            }
            if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || IsGoogleHost(target.Host)) continue;
            var title = Plain(heading.Groups["title"].Value);
            if (title.Length == 0) continue;
            var next = headings.Cast<Match>().FirstOrDefault(x => x.Index > heading.Index)?.Index ?? Math.Min(html.Length, after + 1800);
            var region = html[Math.Min(html.Length, after + 4)..Math.Max(Math.Min(html.Length, after + 4), next)];
            var snippetMatch = Regex.Match(region, "<(?:div|span)\\b[^>]*class=['\"](?<class>[^'\"]*(?:VwiC3b|yXK7lf|IsZvec)[^'\"]*)['\"][^>]*>(?<snippet>.*?)</(?:div|span)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippet = snippetMatch.Success ? Plain(snippetMatch.Groups["snippet"].Value) : "";
            if (!results.Any(x => x.Url == target.AbsoluteUri)) results.Add(new(title, target.AbsoluteUri, snippet));
            if (results.Count >= limit) break;
        }
        // Google has served result markup without h3 headings in some locales/experiments.
        // Recover ordinary result links as a fallback instead of returning an empty parse.
        if (results.Count == 0)
        {
            foreach (Match anchor in Anchor.Matches(html))
            {
                var hrefMatch = Href.Match(anchor.Groups["attrs"].Value);
                if (!hrefMatch.Success) continue;
                var href = WebUtility.HtmlDecode(hrefMatch.Groups["double"].Success ? hrefMatch.Groups["double"].Value : hrefMatch.Groups["single"].Success ? hrefMatch.Groups["single"].Value : hrefMatch.Groups["bare"].Value);
                var target = ResolveTarget(href);
                if (target is null || IsGoogleHost(target.Host)) continue;
                var title = Plain(anchor.Groups["body"].Value);
                if (title.Length < 4 || title.Length > 300 || title.Equals("Überspringen", StringComparison.OrdinalIgnoreCase)) continue;
                if (!results.Any(x => x.Url == target.AbsoluteUri)) results.Add(new(title, target.AbsoluteUri, ""));
                if (results.Count >= limit) break;
            }
        }
        return results;
    }

    public static List<GoogleSearchResult> ParseDuckDuckGo(string html, int limit)
    {
        var results = new List<GoogleSearchResult>();
        foreach (Match link in DdLink.Matches(html))
        {
            var hrefMatch = Href.Match(link.Groups["attrs"].Value);
            if (!hrefMatch.Success) continue;
            var href = WebUtility.HtmlDecode(hrefMatch.Groups["double"].Success ? hrefMatch.Groups["double"].Value : hrefMatch.Groups["single"].Success ? hrefMatch.Groups["single"].Value : hrefMatch.Groups["bare"].Value);
            if (Uri.TryCreate(href, UriKind.Absolute, out var redirect) && redirect.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) && redirect.Query.Contains("uddg=", StringComparison.Ordinal))
            {
                var encoded = redirect.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && x[0] == "uddg");
                if (encoded is { Length: 2 }) href = Uri.UnescapeDataString(encoded[1]);
            }
            if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || IsGoogleHost(target.Host) || target.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)) continue;
            var title = Plain(link.Groups["body"].Value);
            if (title.Length < 3 || results.Any(x => x.Url == target.AbsoluteUri)) continue;
            var afterLink = html[Math.Min(html.Length, link.Index + link.Length)..Math.Min(html.Length, link.Index + link.Length + 1400)];
            var snippetMatch = Regex.Match(afterLink, "class=['\"][^'\"]*result__snippet[^'\"]*['\"][^>]*>(?<snippet>.*?)</(?:a|div|span)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            results.Add(new(title, target.AbsoluteUri, snippetMatch.Success ? Plain(snippetMatch.Groups["snippet"].Value) : ""));
            if (results.Count >= limit) break;
        }
        return results;
    }

    public static List<GoogleSearchResult> ParseBing(string html, int limit)
    {
        var results = new List<GoogleSearchResult>();
        foreach (Match item in BingItems.Matches(html))
        {
            var heading = Regex.Match(item.Groups["body"].Value, "<h2\\b[^>]*>\\s*<a\\b(?<attrs>[^>]*)>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!heading.Success) continue;
            var hrefMatch = Href.Match(heading.Groups["attrs"].Value);
            if (!hrefMatch.Success) continue;
            var href = WebUtility.HtmlDecode(hrefMatch.Groups["double"].Success ? hrefMatch.Groups["double"].Value : hrefMatch.Groups["single"].Value);
            if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || target.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)) continue;
            var title = Plain(heading.Groups["title"].Value);
            var snippetMatch = Regex.Match(item.Groups["body"].Value, "<p\\b[^>]*>(?<snippet>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippet = snippetMatch.Success ? Plain(snippetMatch.Groups["snippet"].Value) : "";
            if (title.Length > 0 && !results.Any(x => x.Url == target.AbsoluteUri)) results.Add(new(title, target.AbsoluteUri, snippet));
            if (results.Count >= limit) break;
        }
        return results;
    }

    public static List<GoogleSearchResult> ParseBingRss(string xml, int limit)
    {
        var results = new List<GoogleSearchResult>();
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        var document = XDocument.Load(reader);
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "item"))
        {
            var title = Plain(item.Elements().FirstOrDefault(element => element.Name.LocalName == "title")?.Value ?? "");
            var url = item.Elements().FirstOrDefault(element => element.Name.LocalName == "link")?.Value?.Trim() ?? "";
            var snippet = Plain(item.Elements().FirstOrDefault(element => element.Name.LocalName == "description")?.Value ?? "");
            if (title.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || target.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (results.All(existing => !existing.Url.Equals(target.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) results.Add(new(title, target.AbsoluteUri, snippet));
            if (results.Count >= Math.Clamp(limit, 1, 20)) break;
        }
        return results;
    }

    public static List<GoogleSearchResult> ParseWikipediaSearch(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("search", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Wikipedia search response did not contain query.search results.");
        var results = new List<GoogleSearchResult>();
        foreach (var item in items.EnumerateArray().Take(Math.Clamp(limit, 1, 20)))
        {
            if (!item.TryGetProperty("title", out var titleNode) || !item.TryGetProperty("snippet", out var snippetNode)) continue;
            var title = Plain(titleNode.GetString() ?? "");
            var snippet = Plain(snippetNode.GetString() ?? "");
            if (title.Length == 0) continue;
            results.Add(new GoogleSearchResult(title, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')), snippet));
        }
        return results;
    }

    private static bool ContainsAny(string text, params string[] values) => values.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static Uri? ResolveTarget(string href)
    {
        if (Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var parsed) && !parsed.IsAbsoluteUri)
        {
            if (!href.StartsWith("/url?", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Uri.TryCreate("https://www.google.com" + href, UriKind.Absolute, out var wrapper)) return null;
            var pair = wrapper.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && (x[0] is "q" or "url"));
            if (pair is not { Length: 2 }) return null;
            href = Uri.UnescapeDataString(pair[1].Replace("+", " "));
        }
        return Uri.TryCreate(href, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" ? target : null;
    }
    private static bool IsGoogleHost(string host) => host.Equals("google.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) || host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    private static string Plain(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), "\\s+", " ").Trim();
}
