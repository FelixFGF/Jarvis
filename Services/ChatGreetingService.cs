using System.Security.Cryptography;

namespace Jarvis.Services;

public sealed record ChatGreeting(string Key, string Title, string Subtitle);

public static class ChatGreetingService
{
    private static readonly string[] German =
    [
        "Wie kann ich dir heute helfen?",
        "Was wollen wir heute zusammen umsetzen?",
        "Welches Projekt nehmen wir uns dieses Mal vor?",
        "Woran arbeitest du gerade?",
        "Was kann ich dir heute abnehmen?"
    ];
    private static readonly string[] English =
    [
        "How can I help you today?",
        "What shall we build together today?",
        "Which project should we tackle this time?",
        "What are you working on?",
        "What can I take off your plate today?"
    ];

    public static string SelectKey(string language, string? previousKey = null, int? deterministicIndex = null)
    {
        var prefix = language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        var count = German.Length;
        var index = deterministicIndex is null ? RandomNumberGenerator.GetInt32(count) : Math.Abs(deterministicIndex.Value % count);
        var previous = ParseIndex(previousKey);
        if (count > 1 && previous == index) index = (index + 1) % count;
        return $"{prefix}.{index}";
    }

    public static ChatGreeting Resolve(string? key, string language, string? discordDisplayName)
    {
        var german = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var list = german ? German : English;
        var index = ParseIndex(key);
        if (index < 0 || index >= list.Length) index = 0;
        var title = list[index];
        var name = NormalizeName(discordDisplayName);
        if (name is not null)
        {
            title = index switch
            {
                0 => german ? $"Wie kann ich dir heute helfen, {name}?" : $"How can I help you today, {name}?",
                3 => german ? $"Woran arbeitest du gerade, {name}?" : $"What are you working on, {name}?",
                _ => title
            };
        }
        return new(key ?? $"{(german ? "de" : "en")}.{index}", title,
            german ? "Ideen, Code und Aufgaben – mit nachvollziehbaren Ergebnissen." : "Ideas, code, and tasks—with results you can verify.");
    }

    private static int ParseIndex(string? key) =>
        int.TryParse(key?.Split('.').LastOrDefault(), out var index) ? index : -1;

    private static string? NormalizeName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) return null;
        return name;
    }
}
