namespace Jarvis.Services;

public sealed record VisibleApplication(int ProcessId, string ProcessName, string WindowTitle, bool IsForeground, bool IsMinimized);
public sealed record ActivityFact(string State, string Confidence, string Evidence);
public sealed record ApplicationActivityReport(
    VisibleApplication? Foreground,
    IReadOnlyList<VisibleApplication> Applications,
    ActivityFact DiscordCall,
    ActivityFact YouTubePlayback,
    ActivityFact Game,
    IReadOnlyList<string> BackgroundProcesses,
    IReadOnlyList<string> ProtectedProcesses,
    IReadOnlyList<string> CloseCandidates);

public static class ApplicationActivityService
{
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "explorer", "dwm", "winlogon", "csrss", "services", "lsass", "svchost", "sihost", "taskhostw", "startmenuexperiencehost", "shellexperiencehost", "securityhealthservice", "securityhealthsystray", "system", "registry" };
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
    private static readonly string[] GameHints = ["fortnite", "minecraft", "roblox", "valorant", "leagueoflegends", "gta", "steam_app", "overwatch", "rocketleague"];

    public static ApplicationActivityReport Analyze(IEnumerable<VisibleApplication> windows, IEnumerable<AudioSessionState> audioSessions, int jarvisProcessId, IEnumerable<string>? backgroundProcesses = null)
    {
        var apps = windows.GroupBy(item => (item.ProcessId, item.WindowTitle)).Select(group => group.First()).ToArray();
        var audio = audioSessions.ToArray();
        var foreground = apps.FirstOrDefault(item => item.IsForeground);
        var discord = apps.Where(item => item.ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase)).ToArray();
        var discordAudio = audio.Any(item => item.ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase) && (item.IsActive || item.PeakValue > .001f));
        var discordFact = discord.Length == 0
            ? new ActivityFact("not_running", "high", "No visible Discord window was found.")
            : new ActivityFact("unknown", "low", discordAudio ? "Discord is running and has audio activity, but that does not prove a call." : "Discord is running; no supported local API confirms its call state.");

        var youtubeWindow = apps.FirstOrDefault(item => Browsers.Contains(item.ProcessName) && item.WindowTitle.Contains("YouTube", StringComparison.OrdinalIgnoreCase));
        var browserAudio = youtubeWindow is not null && audio.Any(item => item.ProcessId == youtubeWindow.ProcessId && (item.IsActive || item.PeakValue > .001f));
        var youtubeFact = youtubeWindow is null
            ? new ActivityFact("not_detected", "medium", "No visible browser window title contains YouTube.")
            : browserAudio
                ? new ActivityFact("probably_playing", "medium", "A YouTube browser window and audio activity from that browser were detected; the exact tab playback state is not confirmed.")
                : new ActivityFact("open_playback_unknown", "medium", "A YouTube browser window was detected, but playback is not confirmed.");

        var gameWindow = apps.FirstOrDefault(item => GameHints.Any(hint => Normalize(item.ProcessName).Contains(hint, StringComparison.Ordinal) || Normalize(item.WindowTitle).Contains(hint, StringComparison.Ordinal)));
        var gameFact = gameWindow is null
            ? new ActivityFact("not_detected", "medium", "No known game process or visible game title was detected.")
            : new ActivityFact(gameWindow.IsForeground ? "foreground" : "running_background", gameWindow.IsForeground ? "high" : "medium", $"{gameWindow.ProcessName}: {gameWindow.WindowTitle}");

        var protectedIds = new HashSet<int> { jarvisProcessId };
        foreach (var app in apps)
        {
            if (app.IsForeground || SystemProcesses.Contains(app.ProcessName) || app == gameWindow || discord.Contains(app) || (youtubeWindow is not null && app.ProcessId == youtubeWindow.ProcessId))
                protectedIds.Add(app.ProcessId);
        }
        foreach (var session in audio.Where(item => item.IsActive || item.PeakValue > .001f)) protectedIds.Add(session.ProcessId);
        var protectedNames = apps.Where(item => protectedIds.Contains(item.ProcessId)).Select(item => item.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var candidates = apps.Where(item => !protectedIds.Contains(item.ProcessId) && !SystemProcesses.Contains(item.ProcessName))
            .Select(item => item.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var visibleNames = apps.Select(item => item.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var background = (backgroundProcesses ?? []).Where(name => !string.IsNullOrWhiteSpace(name) && !visibleNames.Contains(name) && !SystemProcesses.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(24).ToArray();
        return new(foreground, apps, discordFact, youtubeFact, gameFact, background, protectedNames, candidates);
    }

    public static bool IsProtectedProcess(string processName, int processId, int jarvisProcessId) =>
        processId == jarvisProcessId || SystemProcesses.Contains(processName);

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
