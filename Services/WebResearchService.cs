using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace Jarvis.Services;

public enum SearchProviderOutcome { RelevantResults, NoRelevantResults, Blocked, ParseFailed, TimedOut, Cancelled, Unreachable }

public sealed record SearchProviderAttempt(string Provider, SearchProviderOutcome Outcome, string Detail, int ResultCount);

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
        var expanded = GoogleSearchParser.ExpandQuery(query.Trim());
        var attempts = new List<SearchProviderAttempt>();
        var providers = new (string Name, Uri Uri, Func<string, int, List<GoogleSearchResult>> Parse)[]
        {
            ("Bing RSS", new Uri("https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(expanded)), GoogleSearchParser.ParseBingRss),
            ("DuckDuckGo HTML", new Uri("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(expanded)), GoogleSearchParser.ParseDuckDuckGo),
            ("Google HTML", new Uri("https://www.google.com/search?gbv=1&num=10&q=" + Uri.EscapeDataString(expanded)), GoogleSearchParser.Parse)
        };

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_providerTimeout);
            try
            {
                using var response = await _http.GetAsync(provider.Uri, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    attempts.Add(new(provider.Name, response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden ? SearchProviderOutcome.Blocked : SearchProviderOutcome.Unreachable, $"HTTP {(int)response.StatusCode}", 0));
                    continue;
                }
                if (provider.Name == "Google HTML" && (GoogleSearchParser.IsConsentPage(body) || GoogleSearchParser.IsBlockedPage(body)))
                {
                    attempts.Add(new(provider.Name, SearchProviderOutcome.Blocked, "The provider returned a consent or automated-request block page.", 0));
                    continue;
                }
                List<GoogleSearchResult> parsed;
                try { parsed = provider.Parse(body, limit * 2); }
                catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or InvalidDataException)
                {
                    attempts.Add(new(provider.Name, SearchProviderOutcome.ParseFailed, "The response did not match the documented result format.", 0));
                    continue;
                }
                var relevant = GoogleSearchParser.KeepRelevant(query, parsed).Take(limit).ToList();
                attempts.Add(new(provider.Name, relevant.Count > 0 ? SearchProviderOutcome.RelevantResults : SearchProviderOutcome.NoRelevantResults,
                    relevant.Count > 0 ? "Relevant results found." : "The response was valid but contained no sufficiently relevant results.", relevant.Count));
                if (relevant.Count > 0) return new(query, provider.Name, relevant, attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                attempts.Add(new(provider.Name, SearchProviderOutcome.Cancelled, "Cancelled by the caller.", 0));
                throw;
            }
            catch (OperationCanceledException)
            {
                attempts.Add(new(provider.Name, SearchProviderOutcome.TimedOut, "The provider exceeded its time limit.", 0));
            }
            catch (HttpRequestException)
            {
                attempts.Add(new(provider.Name, SearchProviderOutcome.Unreachable, "The provider could not be reached.", 0));
            }
        }
        return new(query, "None", Array.Empty<GoogleSearchResult>(), attempts);
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
