using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Jarvis.Services;

public enum ProviderFailureKind { None, TemporaryRateLimit, QuotaExhausted, TemporaryProviderFailure, Other }

public static class ProviderRateLimitRecovery
{
    public static ProviderFailureKind Classify(HttpStatusCode status, string? body)
    {
        if ((int)status is 408 or 425 or 500 or 502 or 503 or 504) return ProviderFailureKind.TemporaryProviderFailure;
        if ((int)status != 429) return ProviderFailureKind.Other;
        var text = ExtractProviderText(body).ToLowerInvariant();
        if (new[] { "resource_exhausted", "insufficient_quota", "quota_exceeded", "quota exceeded", "exceeded quota", "quota exhausted", "quota has been exceeded", "billing limit", "billing hard limit", "daily quota", "monthly quota", "free tier quota", "exceeded your current quota" }
            .Any(text.Contains)) return ProviderFailureKind.QuotaExhausted;
        return ProviderFailureKind.TemporaryRateLimit;
    }

    public static bool IsTransientTransportFailure(Exception exception, bool requestCancelled = false) =>
        !requestCancelled && exception is HttpRequestException or TimeoutException or TaskCanceledException;

    public static TimeSpan GetRetryDelay(int retryNumber, TimeSpan? retryAfter)
    {
        if (retryAfter is { } explicitDelay)
            return TimeSpan.FromMilliseconds(Math.Clamp(explicitDelay.TotalMilliseconds, 0, 60_000));
        var seconds = Math.Min(8, Math.Pow(2, Math.Clamp(retryNumber, 0, 3)));
        return TimeSpan.FromSeconds(seconds);
    }

    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date - (now ?? DateTimeOffset.UtcNow);
        return null;
    }

    public static bool IsVisionCapabilityFailure(HttpStatusCode status, string? body)
    {
        if ((int)status is not (400 or 415 or 422)) return false;
        var text = ExtractProviderText(body).ToLowerInvariant();
        return (text.Contains("image") || text.Contains("vision") || text.Contains("multimodal"))
            && new[] { "not supported", "does not support", "unsupported", "invalid content", "cannot process", "no image" }.Any(text.Contains);
    }

    public static bool IsToolCallingCapabilityFailure(HttpStatusCode status, string? body)
    {
        if ((int)status is not (400 or 404 or 422)) return false;
        var text = ExtractProviderText(body).ToLowerInvariant();
        return (text.Contains("tool") || text.Contains("function"))
            && new[] { "not supported", "unsupported", "unknown field", "invalid tool", "does not support" }.Any(text.Contains);
    }

    public static bool ShouldRetry(HttpStatusCode status, string? body, int retryNumber, int maxRetries = 3) =>
        Classify(status, body) == ProviderFailureKind.TemporaryRateLimit && retryNumber < Math.Clamp(maxRetries, 0, 5);

    public static bool IsConfirmedOpenAiCompatibleFallback(string? endpoint, string? model, bool capabilitiesConfirmed)
    {
        if (!capabilitiesConfirmed || string.IsNullOrWhiteSpace(model) || !ApiModelCatalogService.IsValidModelId(model.Trim())) return false;
        return Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme is "http" or "https") && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query);
    }

    public static string UserMessage(ProviderFailureKind failure, bool german) => failure switch
    {
        ProviderFailureKind.QuotaExhausted => german
            ? "Das Kontingent dieses KI-Anbieters ist aufgebraucht. Jarvis versucht ein kompatibles, konfiguriertes Fallback-Modell; falls keines verfügbar ist, kannst du die Aufgabe später erneut starten."
            : "This AI provider's quota is exhausted. Jarvis is checking for a compatible configured fallback model; if none is available, you can retry the task later.",
        ProviderFailureKind.TemporaryRateLimit => german
            ? "Der KI-Anbieter begrenzt Anfragen vorübergehend. Jarvis hat die begrenzten Wiederholungen ausgeschöpft; du kannst es erneut versuchen."
            : "The AI provider is temporarily rate-limiting requests. Jarvis used its bounded retries; you can try again.",
        ProviderFailureKind.TemporaryProviderFailure => german
            ? "Der KI-Anbieter ist vorübergehend nicht erreichbar oder meldet einen temporären Serverfehler. Jarvis versucht ein eingerichtetes Fallback, ohne Desktop-Aktionen zu wiederholen."
            : "The AI provider is temporarily unreachable or returned a transient server error. Jarvis will try a configured fallback without repeating desktop actions.",
        _ => german ? "Die Anfrage beim KI-Anbieter ist fehlgeschlagen." : "The AI provider request failed."
    };

    private static string ExtractProviderText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var document = JsonDocument.Parse(body);
            var values = new List<string>();
            Walk(document.RootElement, values, 0);
            return string.Join(' ', values);
        }
        catch (JsonException) { return body.Length > 16_000 ? body[..16_000] : body; }
    }

    private static void Walk(JsonElement element, List<string> values, int depth)
    {
        if (depth > 8 || values.Sum(value => value.Length) > 32_000) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array) Walk(property.Value, values, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Walk(item, values, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.String) values.Add(element.GetString() ?? "");
    }
}
