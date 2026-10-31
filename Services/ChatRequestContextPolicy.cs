using System.Text.RegularExpressions;

namespace Jarvis.Services;

/// <summary>
/// Keeps OpenAI-compatible chat requests within realistic local-model context windows.
/// Tool schemas are selected from the user's current intent instead of attaching the
/// complete agent surface to every conversational request.
/// </summary>
public static class ChatRequestContextPolicy
{
    private static readonly string[] Planning = ["plan_task", "plan_update", "ask_questions"];

    public static IReadOnlySet<string> SelectToolNames(string? userText)
    {
        var text = (userText ?? "").Trim();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (text.Length == 0) return selected;

        void Add(params string[] names) { foreach (var name in names) selected.Add(name); }
        bool Has(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (Has(@"\b(remind|reminder|alarm|erinner|weck|snooze)"))
            Add("ask_questions", "create_reminder", "list_reminders", "cancel_reminder", "snooze_reminder");

        if (Has(@"\b(file|folder|directory|project|workspace|code|source|datei|ordner|verzeichnis|projekt|quellcode|programmier|debug|build|test)"))
        {
            Add(Planning);
            Add("list_files", "search_files", "read_file", "create_folder", "write_file", "request_folder_access", "open_folder");
            if (Has(@"\b(build|test|compile|run|execute|bauen|testen|kompilier|ausführ)"))
                Add("run_powershell", "run_cmd", "command_status", "command_cancel");
        }

        if (Has(@"\b(powershell|power shell|cmd|command prompt|terminal|shell|kommandozeile)"))
            Add("ask_questions", "jarvis_action", "run_powershell", "run_cmd", "command_status", "command_cancel");

        if (Has(@"\b(svg|diagram|diagramm|flowchart|flussdiagramm|architecture diagram|architekturdiagramm|ui mockup|technical illustration|technische illustration|visuali[sz]e|grafik|neural network|neuronales netz)\b"))
            Add("create_or_update_svg");

        if (Has(@"\b(search (?:the )?web|web search|google|research|look up|online|internet|websuche|recherchier|suche im web|nachschlagen)"))
            Add("google_search", "read_web_page", "search_images");

        if (Has(@"\b(browser|website|web page|url|link|chrome|firefox|edge|webseite|internetseite|private browsing|privat(?:es|en)? fenster)"))
            Add("open_url", "open_private_browser", "browser_navigate", "browser_open_tab", "browser_select_tab", "browser_state", "browser_wait", "browser_read_page", "browser_click", "browser_fill", "browser_scroll");

        if (Has(@"\b(volume|mute|unmute|louder|quieter|audio session|lautstärke|lautstaerke|stumm|ton aus|ton an|lauter|leiser)"))
            Add("audio_control");

        if (Has(@"\b(spotify|apple music)"))
            Add("spotify_control", "apple_music_control", "audio_control");

        if (Has(@"\b(minecraft|modrinth|modpack|crash report|crash-log|absturzbericht)"))
            Add(Planning.Concat(["diagnose_minecraft", "find_modrinth_version", "repair_minecraft_mod"]).ToArray());

        var monitorAnalysis = Has(@"\b(analy[sz]e|inspect|describe|compare|read|show|analysier|prüf|beschreib|vergleich|lies|zeig).{0,40}\b(monitors?|screens?|screenshots?|bildschirme?)\b|\b(monitors?|screens?|screenshots?|bildschirme?)\b.{0,40}\b(analy[sz]e|inspect|describe|compare|read|analysier|prüf|beschreib|vergleich|lies)\b");
        var forbidsComputerUse = Has(@"\b(no|without|do not use|don't use|kein|keine|ohne|nicht)\b.{0,25}\b(computer use|computersteuerung)\b");
        if (monitorAnalysis || Has(@"\b(monitors?|screens?|screenshots?|bildschirme?)\b") && forbidsComputerUse)
            Add("analyze_monitors", "inspect_screen_context");
        else if (Has(@"\b(computer use|computersteuerung|click|klick|type|tippe|press|drück|scroll|desktop)"))
        {
            Add(Planning);
            Add("inspect_screen_context", "inspect_desktop", "computer_focus_window", "computer_observe_screen",
                "computer_wait_for_state", "computer_invoke_control", "computer_verify_outcome", "list_windows", "wait_for_window");
            if (Has(@"\b(click|klick)")) Add("computer_click");
            if (Has(@"\b(type|tippe|schreib)")) Add("computer_type_text");
            if (Has(@"\b(press|drück|shortcut|taste|key)")) Add("computer_press_key");
            if (Has(@"\b(scroll)")) Add("computer_scroll");
            if (Has(@"\bcomputer use\b|\bcomputersteuerung\b"))
                Add("computer_click", "computer_type_text", "computer_press_key", "computer_scroll");
        }

        if (Has(@"\b(open|launch|start|close|quit|öffn|oeffn|starte|schließ|schliess|beend).{0,45}\b(app|application|program|notepad|calculator|discord|steam|epic|anwendung|programm|editor|rechner)"))
            Add("ask_questions", "open_app", "list_windows", "wait_for_window", "close_window");

        if (Has(@"\b(what am i doing|running apps|unneeded apps|close everything|foreground app|discord call|youtube|was mache ich|laufende apps|unnötige programme|alles schließen|vordergrund)"))
            Add("inspect_activity", "manage_applications", "list_windows");

        if (Has(@"\b(shutdown|restart|reboot|sign out|log off|lock (?:the )?(?:pc|computer)|herunterfahr|neustart|abmeld|sperr)"))
            Add("critical_system_action");

        if (Has(@"\b(jarvis settings|jarvis einstellungen|auto.?read|auto.?tts|speech output|sprachausgabe|permission|berechtigung)"))
            Add("jarvis_action");

        if (Has(@"\b(hardware|cpu|gpu|ram|memory|storage|grafikkarte|arbeitsspeicher|festplatte)"))
            Add("inspect_hardware");

        // Generic explicit action phrasing gets a small native action set. It does not
        // pull in every file, browser, Computer Use, command, and media schema.
        if (selected.Count == 0 && Has(@"^(?:(?:hey\s+)?jarvis[\s,:!-]*)?(?:please\s+|bitte\s+)?(?:open|launch|start|close|create|run|execute|öffn\w*|oeffn\w*|start\w*|schließ\w*|schliess\w*|erstell\w*|führ\w*)\b"))
            Add("ask_questions", "open_app", "open_folder", "open_url", "list_windows", "wait_for_window");

        return selected;
    }

    public static bool IsContextLimitFailure(string? responseBody)
    {
        var text = (responseBody ?? "").ToLowerInvariant();
        return text.Contains("context_length_exceeded")
            || text.Contains("context length exceeded")
            || text.Contains("exceeds the available context")
            || text.Contains("maximum context length")
            || text.Contains("context size") && (text.Contains("exceed") || text.Contains("too large"))
            || text.Contains("too many tokens")
            || text.Contains("prompt is too long");
    }
}
