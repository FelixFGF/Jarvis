using System.Text.RegularExpressions;

namespace Jarvis.Services;

public static class WebResearchIntentParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static bool TryParse(string text, out string query)
    {
        query = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = Regex.Replace(text.Trim(), @"\s+", " ");
        var google = Regex.Match(compact, @"^(?:bitte\s+)?google\s+(?<query>(?:(?:wie|warum|wo|wann|wer|was|how|why|where|when|who|what)\b|(?:finde?|find|search|suche?|look\s+up|fix|docs?|documentation)\b).+)$", Options);
        if (google.Success)
        {
            query = NormalizeQuery(google.Groups["query"].Value);
            return query.Length is > 0 and <= 300;
        }

        var match = Regex.Match(compact, @"^(?:(?:meine anfrage\s*:\s*)?)(?:bitte\s+)?(?:(?:such(?:e)?|recherchiere|finde)\s+(?:(?:auf\s+)?google(?:\s+nach)?|(?:im\s+(?:web|internet)|online)(?:\s+nach)?)|(?:search|research|find|look up)\s+(?:(?:on\s+)?google(?:\s+for)?|(?:the\s+)?(?:web|internet)(?:\s+for)?))\s+(?<query>.+)$", Options);
        if (!match.Success)
            match = Regex.Match(compact, @"^(?:search|look up)\s+(?<query>(?:how\s+to|this\s+bug|the\s+bug).+)|^(?:such(?:e)?|recherchiere)\s+(?<query>(?:wie\s+man|diesen\s+bug|den\s+bug|nach\s+einer\s+lösung).+)$", Options);
        if (!match.Success) return false;
        query = NormalizeQuery(match.Groups["query"].Value);
        return query.Length is > 0 and <= 300;
    }

    public static bool IsProceedWithResearchRequest(string text) => Regex.IsMatch(
        Regex.Replace(text.Trim(), @"\s+", " "),
        @"(?:^|[.!?]\s*)(?:bitte\s+)?(?:finde|suche|recherchiere|find|search|research|look\s+up)\s+(?:mir\s+)?(?:mögliche\s+)?(?:lösungen?|ursachen?|fix(?:es)?|workarounds?|solutions?|causes?)\b",
        Options);

    public static string ComposeContextualQuery(IEnumerable<string> previousUserMessages, string currentText)
    {
        if (!IsProceedWithResearchRequest(currentText))
            return TryParse(currentText, out var direct) ? direct : NormalizeQuery(currentText);
        var context = previousUserMessages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .TakeLast(3)
            .Select(message => Regex.Replace(message.Trim(), @"\s+", " "))
            .Where(message => !IsProceedWithResearchRequest(message));
        var currentFacts = Regex.Replace(currentText,
            @"(?:^|[.!?]\s*)(?:bitte\s+)?(?:finde|suche|recherchiere|find|search|research|look\s+up)\s+(?:mir\s+)?(?:mögliche\s+)?(?:lösungen?|ursachen?|fix(?:es)?|workarounds?|solutions?|causes?)\b[.!?]*",
            " ", Options);
        var combined = string.Join(" ", context.Append(currentFacts));
        return NormalizeQuery(combined.Length > 300 ? combined[^300..] : combined);
    }

    public static string NormalizeQuery(string value)
    {
        var query = Regex.Replace(value.Trim(), @"[?.!]+$", "").Trim();
        query = Regex.Replace(query, @"^(?:wie\s+man|how\s+to|how\s+do\s+i)\s+", "", Options);
        query = Regex.Replace(query, @"\b(?:fixt|fixen|beheben|behebt)\b", "fix", Options);
        return Regex.Replace(query, @"\s+", " ").Trim();
    }
}
