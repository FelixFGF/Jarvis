namespace Jarvis.Services;

public enum ToolPermissionMode { AlwaysAsk, AutomaticReview, AllowEligible }
public sealed record ToolPermissionModeOption(string Id, string Label, string ToolTip);

public static class ToolPermissionPolicy
{
    public static IReadOnlyList<ToolPermissionModeOption> Options(bool german) =>
    [
        new("ask", german ? "Immer nachfragen" : "Always ask", german ? "Vor geschützten Aktionen fragen" : "Ask before protected actions"),
        new("automatic_review", german ? "Automatisch prüfen" : "Automatic security review", german ? "Geeignete Aktionen durch das konfigurierte Prüfmodell bewerten" : "Review eligible actions with the configured reviewer"),
        new("allow_eligible", german ? "⚠ Alles erlauben" : "⚠ Allow Everything", german ? "Alle technisch unterstützten Jarvis-Aktionen ohne Rückfrage ausführen – einschließlich Löschen" : "Run every technically supported Jarvis action without prompting, including deletion")
    ];
    public static ToolPermissionMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "automatic_review" => ToolPermissionMode.AutomaticReview,
        "allow_eligible" => ToolPermissionMode.AllowEligible,
        _ => ToolPermissionMode.AlwaysAsk
    };

    public static bool IsEligibleForAutomaticApproval(string title, string details, bool requireOneTimeApproval)
    {
        if (requireOneTimeApproval) return false;
        var text = (title + "\n" + details).ToLowerInvariant();
        string[] blocked = ["delete", "löschen", "remove", "security", "sicherheit", "credential", "passwort", "password", "token", "publish", "veröff", "purchase", "kauf", "payment", "admin", "elevat", "format", "registry", "firewall"];
        return !blocked.Any(text.Contains);
    }

    public static bool IsHardBoundary(string title, string details, string? trustScope = null)
    {
        // The Computer Use session warning mentions passwords and tokens to tell
        // the user what not to enter. Those words describe the safety boundary;
        // they do not make screen observation itself a credential operation.
        if (string.Equals(trustScope, "computer-use-session", StringComparison.Ordinal)) return false;
        var text = (title + "\n" + details).ToLowerInvariant();
        string[] blocked = ["delete", "löschen", "remove", "security", "sicherheit", "credential", "passwort", "password", "token", "publish", "veröff", "purchase", "kauf", "payment", "admin", "elevat", "format", "registry", "firewall", "shutdown", "reboot", "uninstall"];
        return blocked.Any(text.Contains);
    }

    public static bool CanRunWithoutPrompt(string? configuredMode, string title, string details,
        string? trustScope = null) => Parse(configuredMode) == ToolPermissionMode.AllowEligible;
}
