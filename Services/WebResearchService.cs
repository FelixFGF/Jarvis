using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace Jarvis.Services;

public enum SearchProviderOutcome { RelevantResults, NoRelevantResults, Blocked, ParseFailed, TimedOut, Cancelled, Unreachable }

public sealed record SearchProviderAttempt(string Provider, SearchProviderOutcome Outcome, string Detail, int ResultCount,
    int? HttpStatus = null, int RawBytes = 0, int RawCount = 0, int ParsedCount = 0, int RetainedCount = 0, string RejectionReason = "", long DurationMs = 0);

public sealed record WebResearchResult(string Query, string Provider, IReadOnlyList<GoogleSearchResult> Results,
    IReadOnlyList<SearchProviderAttempt> Attempts)
{
    public bool IsSuccess => Results.Count > 0;
}

/// <summary>
/// Bounded, provider-independent public search. Provider timeouts are isolated from user cancellation,
/// so a failed provider cannot incorrectly cancel the parent agent run.
/// </summary>
public sealed class WebResearchService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _providerTimeout;

    public WebResearchService(HttpClient? http = null, TimeSpan? providerTimeout = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jarvis/1.1 public-research");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,de;q=0.7");
        _providerTimeout = providerTimeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<WebResearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search query is required.", nameof(query));
        limit = Math.Clamp(limit, 1, 12);
        var researchQuery = WebResearchIntentParser.NormalizeQuery(query);
        var expanded = GoogleSearchParser.ExpandQuery(researchQuery);
        var attempts = new List<SearchProviderAttempt>();
        var retainedResults = new Dictionary<string, GoogleSearchResult>(StringComparer.OrdinalIgnoreCase);
        var providers = new List<(string Name, Uri Uri, Func<string, int, List<GoogleSearchResult>> Parse)>();
        if (GoogleSearchParser.LooksTechnical(researchQuery))
        {
            var issueSearch = System.Text.RegularExpressions.Regex.IsMatch(researchQuery, @"\b(bug|issue|error|crash|problem|fehler)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var endpoint = issueSearch ? "issues" : "repositories";
            var githubQuery = CompactTechnicalQuery(expanded);
            providers.Add(("GitHub Search API", new Uri($"https://api.github.com/search/{endpoint}?q={Uri.EscapeDataString(githubQuery)}&per_page={Math.Min(20, limit * 2)}"), GoogleSearchParser.ParseGitHubSearch));
        }
        if (researchQuery.Contains("minecraft", StringComparison.OrdinalIgnoreCase) || researchQuery.Contains("fabric", StringComparison.OrdinalIgnoreCase) || researchQuery.Contains("forge", StringComparison.OrdinalIgnoreCase))
            providers.Add(("Modrinth API", new Uri($"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(expanded)}&limit={Math.Min(20, limit * 2)}"), GoogleSearchParser.ParseModrinthSearch));
        if (System.Text.RegularExpressions.Regex.IsMatch(researchQuery, @"\b(bug|issue|error|crash|problem|fehler|fix|lösung|workaround)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            providers.Add(("Reddit JSON", new Uri($"https://www.reddit.com/search.json?q={Uri.EscapeDataString(expanded)}&limit={Math.Min(20, limit * 2)}&sort=relevance&raw_json=1"), GoogleSearchParser.ParseRedditSearch));
        providers.AddRange(new (string Name, Uri Uri, Func<string, int, List<GoogleSearchResult>> Parse)[]
        {
            ("Bing RSS", new Uri("https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(expanded)), GoogleSearchParser.ParseBingRss),
            ("DuckDuckGo HTML", new Uri("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(expanded)), GoogleSearchParser.ParseDuckDuckGo),
            ("Google HTML", new Uri("https://www.google.com/search?gbv=1&num=10&q=" + Uri.EscapeDataString(expanded)), GoogleSearchParser.Parse)
        });

        foreach (var provider in providers)
        {
            if (retainedResults.Count >= limit && attempts.Count(attempt => attempt.Outcome == SearchProviderOutcome.RelevantResults) >= 2) break;
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_providerTimeout);
            try
            {
                using var response = await _http.GetAsync(provider.Uri, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    attempts.Add(new(provider.Name, response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden ? SearchProviderOutcome.Blocked : SearchProviderOutcome.Unreachable, $"HTTP {(int)response.StatusCode}", 0,
                        (int)response.StatusCode, System.Text.Encoding.UTF8.GetByteCount(body), DurationMs: stopwatch.ElapsedMilliseconds));
                    continue;
                }
                if (provider.Name == "Google HTML" && (GoogleSearchParser.IsConsentPage(body) || GoogleSearchParser.IsBlockedPage(body)))
                {
                    attempts.Add(new(provider.Name, SearchProviderOutcome.Blocked, "The provider returned a consent or automated-request block page.", 0, (int)response.StatusCode, System.Text.Encoding.UTF8.GetByteCount(body), RejectionReason: "consent-or-automation-block", DurationMs: stopwatch.ElapsedMilliseconds));
                    continue;
                }
                List<GoogleSearchResult> parsed;
                try { parsed = provider.Parse(body, limit * 2); }
                catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or InvalidDataException)
                {
                    attempts.Add(new(provider.Name, SearchProviderOutcome.ParseFailed, "The response did not match the documented result format.", 0, (int)response.StatusCode, System.Text.Encoding.UTF8.GetByteCount(body), RejectionReason: "unexpected-response-schema", DurationMs: stopwatch.ElapsedMilliseconds));
                    continue;
                }
                var relevant = GoogleSearchParser.KeepRelevant(researchQuery, parsed).Take(limit).ToList();
                attempts.Add(new(provider.Name, relevant.Count > 0 ? SearchProviderOutcome.RelevantResults : SearchProviderOutcome.NoRelevantResults,
                    relevant.Count > 0 ? "Relevant results found." : "The response was valid but contained no sufficiently relevant results.", relevant.Count,
                    (int)response.StatusCode, System.Text.Encoding.UTF8.GetByteCount(body), RawCount: parsed.Count, ParsedCount: parsed.Count, RetainedCount: relevant.Count,
                    RejectionReason: relevant.Count == 0 ? (parsed.Count == 0 ? "no-results-parsed" : "relevance-threshold") : "", DurationMs: stopwatch.ElapsedMilliseconds));
                foreach (var hit in relevant)
                {
                    var enriched = hit with { Provider = provider.Name };
                    if (!retainedResults.ContainsKey(enriched.Url)) retainedResults[enriched.Url] = enriched;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                attempts.Add(new(provider.Name, SearchProviderOutcome.Cancelled, "Cancelled by the caller.", 0, DurationMs: stopwatch.ElapsedMilliseconds));
                throw;
            }
            catch (OperationCanceledException)
            {
                attempts.Add(new(provider.Name, SearchProviderOutcome.TimedOut, "The provider exceeded its time limit.", 0, DurationMs: stopwatch.ElapsedMilliseconds));
            }
            catch (HttpRequestException)
            {
                attempts.Add(new(provider.Name, SearchProviderOutcome.Unreachable, "The provider could not be reached.", 0, DurationMs: stopwatch.ElapsedMilliseconds));
            }
        }
        var final = retainedResults.Values.OrderByDescending(result => result.Relevance).Take(limit).ToArray();
        var sources = string.Join(", ", attempts.Where(attempt => attempt.Outcome == SearchProviderOutcome.RelevantResults).Select(attempt => attempt.Provider).Distinct());
        return new(query, final.Length == 0 ? "None" : sources, final, attempts);
    }

    private static string CompactTechnicalQuery(string query)
    {
        var terms = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{N}.]+")
            .Select(match => match.Value)
            .Where(term => !new[] { "bug", "bugs", "issue", "issues", "fix", "crash", "error", "errors", "mod" }.Contains(term, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
        return terms.Length >= 2 ? string.Join(' ', terms) : query;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
