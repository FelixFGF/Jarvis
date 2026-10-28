using System.Globalization;
using System.Text;

namespace Jarvis.Services;

public enum VoiceConversationState { Listening, Processing, Speaking, AwaitingFollowUp, EndingSession }
public enum VoiceFollowUpIntent { EndSession, NewTask, NeedsClarification }

public static class VoiceSessionPolicy
{
    private static readonly Dictionary<string, HashSet<string>> EndPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = Set("nein", "nein danke", "das war alles", "fertig", "mehr brauche ich nicht", "nichts mehr", "das reicht", "das reicht danke"),
        ["en"] = Set("no", "no thanks", "that is all", "that's all", "i am done", "i'm done", "nothing else", "that will be all"),
        ["fr"] = Set("non merci", "c'est tout", "je n'ai besoin de rien d'autre"),
        ["es"] = Set("no gracias", "eso es todo", "nada mas"),
        ["it"] = Set("no grazie", "e tutto", "nient'altro"),
        ["nl"] = Set("nee bedankt", "dat was alles", "verder niets"),
        ["pt"] = Set("nao obrigado", "isso e tudo", "nada mais")
    };
    private static readonly HashSet<string> BareAffirmative = Set("ja", "ja bitte", "yes", "yes please", "klar", "sure", "oui", "si", "sim");
    private static readonly string[] KnownFollowUps =
    [
        "Brauchst du noch etwas?", "Kann ich dir noch bei etwas helfen?", "Soll ich noch etwas für dich erledigen?",
        "Do you need anything else?", "Is there anything else?", "Can I help with anything else?", "Would you like me to do anything else?",
        "Puis-je faire autre chose pour toi ?", "As-tu besoin d'autre chose ?",
        "¿Necesitas algo más?", "¿Puedo ayudarte con algo más?"
    ];

    public static VoiceFollowUpIntent ClassifyFollowUp(string text, string? language = null)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 || BareAffirmative.Contains(normalized)) return VoiceFollowUpIntent.NeedsClarification;
        IEnumerable<HashSet<string>> languages = string.IsNullOrWhiteSpace(language)
            ? EndPhrases.Values
            : EndPhrases.TryGetValue(LanguageCode(language), out var selected) ? new[] { selected } : EndPhrases.Values;
        return languages.Any(set => set.Contains(normalized)) ? VoiceFollowUpIntent.EndSession : VoiceFollowUpIntent.NewTask;
    }

    public static bool ShouldAwaitFollowUp(bool meaningfulToolCompleted, bool taskFailed, bool assistantAskedQuestion) =>
        meaningfulToolCompleted && !taskFailed && assistantAskedQuestion;

    public static bool ShouldOfferMoreHelp(bool voiceMode, int completedTools, int failedTools, string? planStatus) =>
        voiceMode && completedTools > 0 && failedTools == 0 && planStatus is not ("In progress" or "Incomplete" or "Needs confirmation" or "Failed");

    public static string EnsureCompletionFollowUp(string answer, string language, int variationSeed)
    {
        answer = (answer ?? "").Trim();
        if (answer.Length == 0) return answer;
        // Some providers append their own stock English question. Replace that
        // suffix instead of returning a mixed-language answer or adding a second one.
        foreach (var known in KnownFollowUps)
        {
            if (!answer.EndsWith(known, StringComparison.OrdinalIgnoreCase)) continue;
            answer = answer[..^known.Length].TrimEnd();
            break;
        }
        if (answer.Length == 0 || EndsWithQuestion(answer)) return answer;
        var options = LanguageCode(language) switch
        {
            "de" => new[] { "Brauchst du noch etwas?", "Kann ich dir noch bei etwas helfen?", "Soll ich noch etwas für dich erledigen?" },
            "fr" => new[] { "Puis-je faire autre chose pour toi ?", "As-tu besoin d'autre chose ?" },
            "es" => new[] { "¿Necesitas algo más?", "¿Puedo ayudarte con algo más?" },
            _ => new[] { "Do you need anything else?", "Can I help with anything else?", "Would you like me to do anything else?" }
        };
        return answer + " " + options[Math.Abs(variationSeed % options.Length)];
    }

    /// <summary>
    /// Resolves the language for a live voice turn. The current utterance wins;
    /// an ambiguous utterance inherits the session, then an explicit STT language,
    /// and only then the application UI language.
    /// </summary>
    public static string ResolveLanguage(string? utterance, string? sessionLanguage, string? speechLanguage, string? uiLanguage)
    {
        var normalized = Normalize(utterance ?? "");
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var german = Score(words, "ich", "du", "bitte", "danke", "kannst", "offne", "rechner", "einstellungen", "nicht", "noch", "etwas", "war", "alles", "brauche", "fehler");
        var english = Score(words, "i", "you", "please", "thanks", "open", "calculator", "settings", "not", "anything", "else", "that", "all", "need", "error");
        if (german > english) return "de";
        if (english > german) return "en";
        if (!string.IsNullOrWhiteSpace(sessionLanguage)) return LanguageCode(sessionLanguage);
        if (!string.IsNullOrWhiteSpace(speechLanguage) && !speechLanguage.Equals("automatic", StringComparison.OrdinalIgnoreCase)) return LanguageCode(speechLanguage);
        return LanguageCode(string.IsNullOrWhiteSpace(uiLanguage) ? "en" : uiLanguage);
    }

    private static int Score(HashSet<string> words, params string[] indicators) => indicators.Count(words.Contains);

    private static bool EndsWithQuestion(string value) => value.TrimEnd().EndsWith('?') || value.TrimEnd().EndsWith('？');

    public static string Clarification(string language) => LanguageCode(language) switch
    {
        "de" => "Gern. Was soll ich als Nächstes tun?", "fr" => "Bien sûr. Que dois-je faire ensuite ?",
        "es" => "Claro. ¿Qué quieres que haga ahora?", _ => "Sure. What would you like me to do next?"
    };

    public static string Farewell(string language) => LanguageCode(language) switch
    {
        "de" => "Alles klar. Du kannst mich jederzeit wieder rufen. Bis dann!", "fr" => "Très bien. Tu peux me rappeler à tout moment. À bientôt !",
        "es" => "De acuerdo. Puedes llamarme cuando quieras. ¡Hasta luego!", "it" => "Va bene. Puoi richiamarmi quando vuoi. A presto!",
        "nl" => "Prima. Je kunt me altijd weer roepen. Tot later!", "pt" => "Tudo bem. Pode me chamar novamente quando quiser. Até logo!",
        _ => "All right. You can call me again any time. See you!"
    };

    private static string LanguageCode(string value) => value.Trim().Split('-', '_')[0].ToLowerInvariant();
    private static HashSet<string> Set(params string[] values) => new(values.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var plain = new string(decomposed.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
        return string.Join(' ', new string(plain.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '\'' ? ch : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
