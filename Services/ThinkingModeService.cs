using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Services;

public enum ThinkingMode { Fast, Balanced, Deep }

public sealed record ThinkingRequestControl(
    ThinkingMode Mode,
    bool IsApplied,
    string? ReasoningEffort,
    string SupportKind,
    string Detail);

public sealed record ThinkingModePresentation(string Icon, string Label, string Description);

public static class ThinkingModeService
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, ThinkingRequestControl Control)> OllamaControlCache = new(StringComparer.OrdinalIgnoreCase);

    public static ThinkingMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fast" => ThinkingMode.Fast,
        "deep" or "deep_thinking" => ThinkingMode.Deep,
        _ => ThinkingMode.Balanced
    };

    public static string Serialize(ThinkingMode mode) => mode switch
    {
        ThinkingMode.Fast => "fast",
        ThinkingMode.Deep => "deep",
        _ => "balanced"
    };

    public static ThinkingModePresentation Present(ThinkingMode mode, bool german) => mode switch
    {
        ThinkingMode.Fast => new("⚡", german ? "Schnell" : "Fast",
            german ? "Priorisiert kurze Reaktionszeiten für einfache Aufgaben." : "Prioritizes response speed for simple tasks."),
        ThinkingMode.Deep => new("🧠", german ? "Tief" : "Deep",
            german ? "Fordert höheren Reasoning-Aufwand für schwierige Aufgaben an." : "Requests higher reasoning effort for difficult tasks."),
        _ => new("⚖", german ? "Ausgewogen" : "Balanced",
            german ? "Gleicht Reaktionszeit und Antwortqualität aus." : "Balances response speed and answer quality.")
    };

    public static async Task<ThinkingRequestControl> ResolveAsync(HttpClient http, string endpoint, string model,
        ThinkingMode mode, string? apiKey, CancellationToken token)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(model))
            return Unsupported(mode, "The configured endpoint or model is invalid.");

        if (IsOllamaEndpoint(uri))
        {
            var cacheKey = $"{uri.Scheme}://{uri.Authority}|{model.Trim()}|{mode}";
            if (OllamaControlCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Control;
            try
            {
                var showUri = new UriBuilder(uri) { Path = "/api/show", Query = "", Fragment = "" }.Uri;
                using var request = new HttpRequestMessage(HttpMethod.Post, showUri)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { model = model.Trim() }), Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return Unsupported(mode, $"Ollama did not expose thinking metadata (HTTP {(int)response.StatusCode}).");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("thinking", out var thinking) || thinking.ValueKind != JsonValueKind.Object
                    || !thinking.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                    return Unsupported(mode, "This Ollama model does not advertise thinking controls.");

                var names = values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).ToArray();
                var booleans = values.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    .Select(item => item.GetBoolean()).Distinct().ToArray();
                if (names.Length == 0 && booleans.SequenceEqual(new[] { false }))
                    return Unsupported(mode, "This Ollama model explicitly disables thinking controls.");

                var effort = SelectOllamaEffort(mode, names);
                if (effort is null) return Unsupported(mode, booleans.Contains(true)
                    ? "This Ollama model exposes only native boolean thinking, which cannot be mapped safely onto its OpenAI-compatible reasoning_effort request."
                    : "This Ollama model has no compatible level for the selected mode.");
                var control = new ThinkingRequestControl(mode, true, effort, "ollama",
                    $"Ollama /api/show confirmed the model control; reasoning_effort={effort}.");
                OllamaControlCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(10), control);
                return control;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                return Unsupported(mode, "Ollama thinking capabilities could not be verified; the request remains compatible.");
            }
        }

        if (IsOfficialOpenAiEndpoint(uri) && IsOpenAiReasoningModel(model))
        {
            var effort = mode switch { ThinkingMode.Fast => "low", ThinkingMode.Deep => "high", _ => "medium" };
            return new ThinkingRequestControl(mode, true, effort, "openai", $"OpenAI reasoning_effort={effort}.");
        }

        return Unsupported(mode, "This OpenAI-compatible endpoint does not advertise a verified reasoning control.");
    }

    public static JsonObject Apply(JsonObject payload, ThinkingRequestControl control)
    {
        payload.Remove("reasoning_effort");
        payload.Remove("reasoning");
        if (control.IsApplied && !string.IsNullOrWhiteSpace(control.ReasoningEffort))
            payload["reasoning_effort"] = control.ReasoningEffort;
        return payload;
    }

    public static bool IsOllamaEndpoint(Uri uri) =>
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase)
        ? uri.Port == 11434
        : uri.Host.Equals("ollama.com", StringComparison.OrdinalIgnoreCase)
          || uri.Host.EndsWith(".ollama.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsOpenAiReasoningModel(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(id, @"^(o1|o3|o4)(?:[-:]|$)")
            || id.StartsWith("gpt-5", StringComparison.Ordinal)
            || id.StartsWith("gpt-6", StringComparison.Ordinal);
    }

    private static bool IsOfficialOpenAiEndpoint(Uri uri) =>
        uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase);

    private static string? SelectOllamaEffort(ThinkingMode mode, IReadOnlyList<string> names)
    {
        static string? First(IReadOnlyList<string> values, params string[] preferred) =>
            preferred.Select(item => values.FirstOrDefault(value => value.Equals(item, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(value => value is not null);
        if (names.Count > 0)
            return mode switch
            {
                ThinkingMode.Fast => First(names, "none", "minimal", "low") ?? names[0],
                ThinkingMode.Deep => First(names, "high", "max", "xhigh", "ultra") ?? names[^1],
                _ => First(names, "medium", "balanced") ?? names[Math.Min(names.Count / 2, names.Count - 1)]
            };
        return null;
    }

    private static ThinkingRequestControl Unsupported(ThinkingMode mode, string detail) =>
        new(mode, false, null, "unsupported", detail);
}
