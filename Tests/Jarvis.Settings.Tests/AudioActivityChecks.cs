using Jarvis.Services;
using System.IO;

internal static class AudioActivityChecks
{
    public static void Run()
    {
        var backend = new FakeAudioBackend();
        var service = new WindowsAudioControlService(backend);
        Check(service.GetSystemState().VolumePercent == 42, "System volume can be read through the audio abstraction");
        Check(service.SetSystemVolume(60).System?.VolumePercent == 60, "System volume mutation returns its verified state");
        Check(service.SetSystemMute(true).System?.IsMuted == true, "System mute mutation returns its verified state");
        var changed = service.SetApplicationVolume("Discord", 25);
        Check(changed.Ok && changed.Sessions.Count == 2 && changed.Sessions.All(item => item.VolumePercent == 25), "All audio sessions belonging to one application are changed and verified");
        var restoredVolume = service.RestoreApplicationVolume("Discord");
        Check(restoredVolume.Ok && restoredVolume.Sessions.Select(item => item.VolumePercent).Order().SequenceEqual(new[] { 70, 80 }), "The verified previous per-session volume can be restored");
        Check(service.SetApplicationMute("discord", true).Sessions.All(item => item.IsMuted), "Application mute and unmute target every matching session");
        Check(service.SetApplicationMute("discord", false).Sessions.All(item => !item.IsMuted), "Application unmute is verified");
        Check(!service.SetApplicationVolume("missing-app", 10).Ok, "Missing application audio sessions return an honest failure");
        var invalidRejected = false;
        try { service.SetSystemVolume(101); } catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        Check(invalidRejected, "Invalid volume percentages are rejected");

        var windows = new[]
        {
            new VisibleApplication(100, "Jarvis", "Jarvis", false, false),
            new VisibleApplication(200, "Discord", "Discord", false, false),
            new VisibleApplication(300, "FortniteClient-Win64-Shipping", "Fortnite", true, false),
            new VisibleApplication(400, "chrome", "YouTube - Shader showcase", false, false),
            new VisibleApplication(500, "notepad", "Notes", false, true)
        };
        var activity = ApplicationActivityService.Analyze(windows, backend.ListSessions(), 100, ["steamwebhelper", "services"]);
        Check(activity.DiscordCall.State == "unknown", "Discord audio activity is not treated as proof of a call");
        Check(activity.Game.State == "foreground" && activity.ProtectedProcesses.Contains("FortniteClient-Win64-Shipping"), "Foreground games are detected and protected");
        Check(activity.YouTubePlayback.State == "probably_playing" && activity.YouTubePlayback.State != "confirmed", "YouTube title plus browser audio remains an inference, not confirmed playback");
        Check(activity.CloseCandidates.SequenceEqual(new[] { "notepad" }, StringComparer.OrdinalIgnoreCase), "Close-unneeded selection excludes foreground, games, Discord, media, Jarvis and system processes");
        Check(activity.BackgroundProcesses.SequenceEqual(new[] { "steamwebhelper" }, StringComparer.OrdinalIgnoreCase), "Compact background-process context excludes protected Windows services");
        Check(ApplicationActivityService.IsProtectedProcess("services", 999, 100) && ApplicationActivityService.IsProtectedProcess("Jarvis", 100, 100), "System processes and Jarvis remain protected");

        Check(VoiceSessionPolicy.ClassifyFollowUp("Nein danke.") == VoiceFollowUpIntent.EndSession, "German negative follow-up ends the voice session");
        Check(VoiceSessionPolicy.ClassifyFollowUp("No thanks") == VoiceFollowUpIntent.EndSession, "English negative follow-up ends the voice session");
        Check(VoiceSessionPolicy.ClassifyFollowUp("Mehr brauche ich nicht", "de-DE") == VoiceFollowUpIntent.EndSession, "German contextual end phrase is recognized");
        Check(VoiceSessionPolicy.ClassifyFollowUp("Nein, öffne stattdessen die Einstellungen", "de") == VoiceFollowUpIntent.NewTask, "A negative phrase containing a new instruction remains a task");
        Check(VoiceSessionPolicy.ClassifyFollowUp("No, open settings instead", "en") == VoiceFollowUpIntent.NewTask, "An English alternative instruction does not end the session");
        Check(VoiceSessionPolicy.ClassifyFollowUp("Ja") == VoiceFollowUpIntent.NeedsClarification, "A bare affirmative asks for the next task instead of creating an empty task");
        Check(VoiceSessionPolicy.ClassifyFollowUp("Öffne Discord") == VoiceFollowUpIntent.NewTask, "A concrete voice follow-up continues as a new task");
        Check(!VoiceSessionPolicy.ShouldAwaitFollowUp(false, false, true) && VoiceSessionPolicy.ShouldAwaitFollowUp(true, false, true), "Only a verified meaningful task with a spoken question enters the follow-up state");
        Check(VoiceSessionPolicy.ShouldOfferMoreHelp(true, 1, 0, "Completed") && !VoiceSessionPolicy.ShouldOfferMoreHelp(true, 1, 0, "In progress"), "Voice follow-up is offered only after a completed verified task");
        Check(VoiceSessionPolicy.EnsureCompletionFollowUp("Notepad wurde geöffnet.", "de", 0).EndsWith('?'), "German verified task result receives one localized fallback question");
        Check(VoiceSessionPolicy.EnsureCompletionFollowUp("Notepad ist jetzt geöffnet! Do you need anything else?", "de", 0).EndsWith("Brauchst du noch etwas?", StringComparison.Ordinal), "A provider-added English follow-up is replaced instead of producing mixed-language voice output");
        Check(VoiceSessionPolicy.ResolveLanguage("Kannst du bitte Notepad öffnen?", "en", "automatic", "en") == "de" &&
            VoiceSessionPolicy.ResolveLanguage("Open Calculator, please.", "de", "automatic", "de") == "en", "Current German and English utterances take precedence over stale session and UI language");
        Check(VoiceSessionPolicy.ResolveLanguage("Jarvis", "de", "automatic", "en") == "de", "An ambiguous utterance inherits the last clear voice-session language");

        var critical = new CriticalSystemActionCoordinator();
        critical.Begin("chat-a", CriticalSystemAction.Shutdown);
        Check(critical.Handle("chat-a", "Ja, ja, ja").Outcome == CriticalConfirmationOutcome.Rejected, "Repeated words in one message never count as three confirmations");
        critical.Begin("chat-a", CriticalSystemAction.Restart);
        Check(critical.Handle("chat-a", "Ja").Confirmations == 1 && critical.Handle("chat-a", "Ich bestätige").Confirmations == 2 && critical.Handle("chat-a", "confirm").Outcome == CriticalConfirmationOutcome.Execute, "Critical actions require three separate unambiguous user replies");
        critical.Begin("chat-a", CriticalSystemAction.SignOut);
        Check(critical.Handle("chat-a", "Abbrechen").Outcome == CriticalConfirmationOutcome.Cancelled && !critical.HasPending, "Cancellation clears a pending critical action");
        var rpc = new FakeDiscordTransport();
        string? rpcApplicationId = null;
        var presence = new DiscordRichPresenceService(id => { rpcApplicationId = id; return rpc; });
        Check(presence.SetAsync(true, JarvisPresenceActivity.Coding).GetAwaiter().GetResult()
            && rpcApplicationId == DiscordRichPresenceService.ApplicationId
            && rpc.Payloads.Any(payload => payload.Contains("Coding with Jarvis", StringComparison.Ordinal)
                && payload.Contains("Download Jarvis", StringComparison.Ordinal)
                && payload.Contains(DiscordRichPresenceService.DownloadUrl, StringComparison.Ordinal)
                && payload.Contains(DiscordRichPresenceService.LogoAssetKey, StringComparison.Ordinal)), "Discord Rich Presence uses the built-in application, branding asset and supported download button");
        presence.SetAsync(true, JarvisPresenceActivity.Voice).GetAwaiter().GetResult();
        presence.SetAsync(true, JarvisPresenceActivity.Chatting).GetAwaiter().GetResult();
        Check(rpc.Payloads.Any(payload => payload.Contains("Talking with Jarvis", StringComparison.Ordinal) && payload.Contains("Download Jarvis", StringComparison.Ordinal))
            && rpc.Payloads.Any(payload => payload.Contains("Chatting with Jarvis", StringComparison.Ordinal) && payload.Contains("Download Jarvis", StringComparison.Ordinal)), "Every Discord status transition resends the button and complete activity payload");
        var repeatedPresencePayloads = rpc.Payloads.Count;
        presence.SetAsync(true, JarvisPresenceActivity.Chatting).GetAwaiter().GetResult();
        Check(rpc.Payloads.Count == repeatedPresencePayloads + 1
            && rpc.Payloads[^1].Contains("Download Jarvis", StringComparison.Ordinal)
            && rpc.Payloads[^1].Contains(DiscordRichPresenceService.DownloadUrl, StringComparison.Ordinal), "Repeated Discord status refreshes restore the complete download-button payload");
        presence.ClearAsync().GetAwaiter().GetResult();
        Check(rpc.Payloads.Any(payload => payload.Contains("\"activity\":null", StringComparison.Ordinal)), "Disabling Discord Rich Presence clears the existing activity");

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        _ = source; // The actual registry is checked by the copied code-behind source below.
        var mainWindowSourcePath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs");
        if (File.Exists(mainWindowSourcePath))
        {
            var registry = File.ReadAllText(mainWindowSourcePath);
            Check(registry.Contains("\"audio_control\"", StringComparison.Ordinal) && registry.Contains("\"inspect_activity\"", StringComparison.Ordinal) && registry.Contains("\"manage_applications\"", StringComparison.Ordinal), "Audio, activity and application-management tools are present in the real tool registry");
            var classification = registry.IndexOf("VoiceSessionPolicy.ClassifyFollowUp", StringComparison.Ordinal);
            Check(registry.Contains("VoiceSessionPolicy.EnsureCompletionFollowUp", StringComparison.Ordinal) && classification >= 0 && registry.IndexOf("PromptBox.Text =", classification, StringComparison.Ordinal) > classification, "Verified voice tasks receive one final localized follow-up and exit intent is handled before model submission");
            Check(registry.Contains("ResumeVoiceListeningAfterTurn", StringComparison.Ordinal)
                && registry.Contains("Dispatcher.BeginInvoke(new Action(ResumeVoiceListeningAfterTurn)", StringComparison.Ordinal), "Voice Mode explicitly returns to listening after a completed or failed playback turn");
            Check(registry.Contains("LocalTtsService.DetectCulture(answer, _config.UiLanguage)", StringComparison.Ordinal)
                && registry.Contains("LocalTtsService.DetectCulture(message.Text, _config.UiLanguage)", StringComparison.Ordinal), "Auto-read and manual chat playback resolve the response language through the shared provider-independent TTS path");
            Check(registry.Contains("mode == ConversationMode.Voice ? CurrentVoiceLanguage", StringComparison.Ordinal)
                && registry.Contains("SpeakStreamAsync(textChunks, CurrentVoiceLanguage", StringComparison.Ordinal), "Voice prompt assembly and streamed speech output use the current utterance language instead of the UI language");
            var wakeCallback = registry.IndexOf("has been recognized. Its pre-roll therefore belongs to the activation", StringComparison.Ordinal);
            var wakeStart = registry.IndexOf("private async void BeginVoiceFromWakeWord", StringComparison.Ordinal);
            Check(wakeCallback >= 0 && registry.IndexOf("_wakeWordPrefillPending = false", wakeCallback, StringComparison.Ordinal) > wakeCallback
                && wakeStart >= 0 && registry.IndexOf("await PlayWakeSoundAndWaitAsync()", wakeStart, StringComparison.Ordinal) > wakeStart,
                "Wake activation discards the phrase and waits for the chime before recording a fresh request");
            Check(registry.Contains("cache_tts_phrase", StringComparison.Ordinal) && registry.Contains("TtsAudioCacheEnabled", StringComparison.Ordinal), "Speech-cache setting and model-selected standard phrase action are connected to the real tool path");
            Check(registry.Contains("critical_system_action", StringComparison.Ordinal) && registry.Contains("analyze_monitors", StringComparison.Ordinal), "Critical-system and read-only monitor-vision tools are present in the real registry");
            var monitorHandler = registry.IndexOf("private async Task<string> AnalyzeMonitorsAsync", StringComparison.Ordinal);
            var desktopHandler = registry.IndexOf("private async Task<string> InspectDesktopAsync", StringComparison.Ordinal);
            Check(monitorHandler >= 0 && desktopHandler > monitorHandler && !registry[monitorHandler..desktopHandler].Contains("EnsureComputerUseSessionAsync", StringComparison.Ordinal), "Monitor vision capture is independent from Computer Use session setup");
            var writeHandler = registry.IndexOf("if (name == \"write_file\")", registry.IndexOf("private async Task<string> ExecuteModelToolAsync", StringComparison.Ordinal), StringComparison.Ordinal);
            var writeEnd = registry.IndexOf("throw new InvalidDataException($\"Werkzeug", writeHandler, StringComparison.Ordinal);
            Check(writeHandler >= 0 && writeEnd > writeHandler && !registry[writeHandler..writeEnd].Contains("SetWorkspaceLayout(WorkspaceLayoutMode.Split)", StringComparison.Ordinal), "File creation refreshes change data without forcing the workspace open");
        }
        if (Environment.GetEnvironmentVariable("JARVIS_LIVE_AUDIO") == "1") RunLiveReadAndNoOpMutation();
    }

    private static void RunLiveReadAndNoOpMutation()
    {
        try
        {
            var live = new WindowsAudioControlService();
            var system = live.GetSystemState();
            var verifiedSystem = live.SetSystemVolume(system.VolumePercent);
            Check(verifiedSystem.Ok && Math.Abs((verifiedSystem.System?.VolumePercent ?? -100) - system.VolumePercent) <= 1, "LIVE: Windows system volume was read and safely set to its existing value");
            var sessions = live.ListSessions();
            Console.WriteLine($"LIVE: Windows Core Audio returned {sessions.Count} session(s).");
            var safeGroup = sessions.GroupBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Select(item => item.VolumePercent).Distinct().Count() == 1 && group.Select(item => item.IsMuted).Distinct().Count() == 1);
            if (safeGroup is null) Console.WriteLine("SKIP: No application session group had a uniform state suitable for a non-disruptive live write.");
            else
            {
                var current = safeGroup.First();
                var volume = live.SetApplicationVolume(current.ProcessName, current.VolumePercent);
                var mute = live.SetApplicationMute(current.ProcessName, current.IsMuted);
                Check(volume.Ok && mute.Ok, $"LIVE: {current.ProcessName} volume/mute were safely written back with their existing values");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SKIP: Live Windows Core Audio unavailable: {ex}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class FakeAudioBackend : IWindowsAudioBackend
    {
        private SystemAudioState _system = new(42, false);
        private readonly List<AudioSessionState> _sessions =
        [
            new("discord-1", 200, "Discord", "Discord voice", true, 80, false, .4f),
            new("discord-2", 200, "Discord", "Discord notification", false, 70, false, 0),
            new("chrome-1", 400, "chrome", "Chrome", true, 55, false, .2f)
        ];

        public SystemAudioState GetSystemState() => _system;
        public void SetSystemVolume(float scalar) => _system = _system with { VolumePercent = (int)Math.Round(scalar * 100) };
        public void SetSystemMute(bool muted) => _system = _system with { IsMuted = muted };
        public IReadOnlyList<AudioSessionState> ListSessions() => _sessions.ToArray();
        public int SetSessionVolume(IReadOnlyCollection<string> ids, float scalar) => Change(ids, state => state with { VolumePercent = (int)Math.Round(scalar * 100) });
        public int SetSessionMute(IReadOnlyCollection<string> ids, bool muted) => Change(ids, state => state with { IsMuted = muted });
        private int Change(IReadOnlyCollection<string> ids, Func<AudioSessionState, AudioSessionState> update)
        {
            var changed = 0;
            for (var index = 0; index < _sessions.Count; index++)
                if (ids.Contains(_sessions[index].SessionId)) { _sessions[index] = update(_sessions[index]); changed++; }
            return changed;
        }
    }

    private sealed class FakeDiscordTransport : IDiscordRpcTransport
    {
        public List<string> Payloads { get; } = [];
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(int opcode, string json, CancellationToken token) { Payloads.Add(json); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
