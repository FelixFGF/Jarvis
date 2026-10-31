using System.Text.RegularExpressions;

namespace Jarvis.Services;

public enum RequestComplexityLevel { SimpleConversation = 1, NormalQuestion = 2, ComplexTask = 3 }

public sealed record RequestComplexityDecision(RequestComplexityLevel Level, int HistoryCharacterBudget, bool IncludeRuntimeContext, ThinkingMode EffectiveThinkingMode);

public static class RequestComplexityPolicy
{
    private static readonly Regex SimpleConversation = new(
        @"^(?:(?:hey|hi|hallo|moin|servus|guten\s+(?:morgen|tag|abend)|hello|good\s+(?:morning|afternoon|evening))\b[\s,!.?]*(?:(?:jarvis)\b[\s,!.?]*)?(?:(?:wie\s+(?:geht(?:'s|\s+es)?\s*(?:dir)?|läuft(?:'s)?|sieht(?:'s)?\s+aus)|was\s+geht|what(?:'s|\s+is)\s+up|how\s+are\s+you)\b[\s,!.?]*)?|(?:wie\s+(?:geht(?:'s|\s+es)?\s*(?:dir)?|läuft(?:'s)?)|how\s+are\s+you|how(?:'s|\s+is)\s+it\s+going|danke|dankeschön|thanks|thank\s+you|alles\s+klar|okay|ok|passt|cool|nice|was\s+machst\s+du|what\s+are\s+you\s+doing)\b[\s,!.?]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ComplexSignals = new(
        @"\b(analy[sz]e|implement|fix|debug|build|test|research|project|code|workspace|file|folder|svg|diagram|flowchart|browser|computer\s+use|terminal|powershell|cmd|monitor|create|modify|delete|install|analysier|implementier|reparier|beheb|baue|teste|recherchier|projekt|quellcode|datei|ordner|diagramm|grafik|computersteuerung|erstelle|ändere|lösche|installier)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RequestComplexityDecision Classify(string? userText, ThinkingMode selectedMode)
    {
        var text = Regex.Replace((userText ?? "").Trim(), @"\s+", " ");
        if (text.Length is > 0 and <= 100 && SimpleConversation.IsMatch(text))
            return new(RequestComplexityLevel.SimpleConversation, 1_800, false, ThinkingMode.Fast);
        if (text.Length > 500 || ComplexSignals.IsMatch(text))
            return new(RequestComplexityLevel.ComplexTask, 10_000, true, selectedMode);
        return new(RequestComplexityLevel.NormalQuestion, 8_000, false, selectedMode);
    }

    public static string BuildSimpleSystemPrompt(bool german) => german
        ? "Du bist Jarvis. Antworte auf diese lockere Unterhaltung direkt, natürlich und freundlich auf Deutsch. Verwende meist genau einen kurzen Satz. Keine Überschrift, Liste, Planung, Werkzeuge oder erfundete Aktion. Gib keine private Gedankenkette aus."
        : "You are Jarvis. Reply to this casual conversation directly, naturally, and warmly in English. Usually use exactly one short sentence. No heading, list, planning, tools, or invented action. Do not reveal private chain-of-thought.";

    public static int EstimateInputTokens(IEnumerable<string> messageTexts) =>
        (int)Math.Ceiling(messageTexts.Sum(text => text?.Length ?? 0) / 3.6d);
}
