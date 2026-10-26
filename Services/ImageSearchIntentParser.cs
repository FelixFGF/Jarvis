using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

public static class ImageSearchIntentParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static bool TryParse(string text, out string query)
    {
        query = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = Regex.Replace(text.Trim(), @"\s+", " ");
        var hasImageSubject = Regex.IsMatch(compact, @"\b(?:bilder|fotos|images?|photos?|pictures?|screenshots?|logos?|shaders?)\b", Options);
        var hasRequestVerb = Regex.IsMatch(compact, @"^(?:bitte\s+)?(?:suche|such|finde|zeig(?:e)?|search|find|show|look\s+up)\b", Options);
        var startsWithImageRequest = Regex.IsMatch(compact, @"^(?:bitte\s+)?(?:bilder|fotos|images?|photos?|pictures?|screenshots?|logos?)\s+(?:von|vom|zu|über|of|for|about)\b", Options);
        if (!hasImageSubject || (!hasRequestVerb && !startsWithImageRequest)) return false;

        var value = Regex.Replace(compact, @"^(?:bitte\s+)?(?:suche|such|finde|zeig(?:e)?|search|find|show|look\s+up)\b\s*", "", Options);
        value = Regex.Replace(value, @"^(?:mir|uns|me|us)\s+", "", Options);
        value = Regex.Replace(value, @"^(?:(?:im|in\s+the)\s+(?:web|internet)\s+(?:nach|for)\s+|(?:the\s+)?web\s+for\s+)", "", Options);
        value = Regex.Replace(value, @"^(?:for|nach)\s+", "", Options);

        var leadingLogo = Regex.Match(value, @"^(?:das|the)?\s*logos?\s+(?:von|vom|of|for)\s+(?<topic>.+)$", Options);
        if (leadingLogo.Success)
            value = leadingLogo.Groups["topic"].Value + " logo";
        else
            value = Regex.Replace(value, @"^(?:(?:die|the)\s+)?(?:bilder|fotos|images?|photos?|pictures?|screenshots?)\s+(?:von|vom|zu|über|for|of|about)\s+", "", Options);

        value = Regex.Replace(value, @"^(?:das|die|der|den|the)\s+", "", Options);
        value = Regex.Replace(value, @"\s+(?:und|and)\s+(?:zeig(?:e)?|show)\b.*$", "", Options);
        value = Regex.Replace(value, @"\s+(?:bilder|fotos|images?|photos?|pictures?)\s*$", "", Options);
        value = Regex.Replace(value, @"^(?:the\s+)?official\s+", "official ", Options);
        value = Regex.Replace(value, @"\bminecraft\b", "Minecraft", Options);
        value = NormalizeTopic(value);
        if (value.Length is 0 or > 200) return false;
        query = value;
        return true;
    }

    public static string CreateToolArguments(string query, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new ArgumentException("An image search query between 1 and 200 characters is required.", nameof(query));
        return JsonSerializer.Serialize(new { query = query.Trim(), limit = Math.Clamp(limit, 1, 12) });
    }

    public static string NormalizeTopic(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        normalized = Regex.Replace(normalized, @"^(?:mir|uns|me|us)\s+(?:das|die|den|the)\s+", "", Options);
        normalized = Regex.Replace(normalized, @"^(?:das|die|der|den|the)\s+", "", Options);
        normalized = Regex.Replace(normalized, @"\boffizielle[nrms]?\b", "official", Options);
        normalized = Regex.Replace(normalized, @"[-\s]+logos?\b", " logo", Options);
        normalized = Regex.Replace(normalized, @"\bminecraft\b", "Minecraft", Options);
        return normalized.Trim().TrimEnd('.', '!', '?');
    }
}
