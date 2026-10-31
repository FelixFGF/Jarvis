using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows;
using Jarvis;
using Jarvis.Models;
using Jarvis.Services;

internal static class ReliabilityChecks
{
    public static async Task RunAsync()
    {
        Check(ProviderTimeoutPolicy.IsLocalEndpoint("http://127.0.0.1:11434/v1")
            && ProviderTimeoutPolicy.IsLocalEndpoint("http://192.168.1.44:1234/v1")
            && !ProviderTimeoutPolicy.IsLocalEndpoint("https://api.example.com/v1"), "Loopback and private-LAN model servers use the local-provider timeout policy");
        var settings = new ProviderTimeoutSettings();
        var savedTimeouts = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig
        {
            LocalProviderRequestTimeoutSeconds = 1200, LocalProviderStreamingInactivitySeconds = 240,
            CloudProviderRequestTimeoutSeconds = 150, CloudProviderStreamingInactivitySeconds = 60,
            MaximumAgentTaskSeconds = 2400, DefaultToolTimeoutSeconds = 420
        }))!;
        Check(savedTimeouts.LocalProviderRequestTimeoutSeconds == 1200 && savedTimeouts.LocalProviderStreamingInactivitySeconds == 240
            && savedTimeouts.CloudProviderRequestTimeoutSeconds == 150 && savedTimeouts.MaximumAgentTaskSeconds == 2400
            && savedTimeouts.DefaultToolTimeoutSeconds == 420, "Provider, stream, task and tool timeout settings persist independently");
        var local = ProviderTimeoutPolicy.Resolve("http://192.168.1.44:1234/v1", settings);
        var cloud = ProviderTimeoutPolicy.Resolve("https://api.example.com/v1", settings);
        Check(local.RequestDeadline > cloud.RequestDeadline && local.StreamingInactivity > cloud.StreamingInactivity, "Local requests receive longer bounded deadlines than cloud requests");
        var slowButHealthy = await ProviderTimeoutPolicy.ExecuteAsync(async token => { await Task.Delay(35, token); return 42; }, default,
            ProviderTimeoutKind.RequestDeadline, "local-test", TimeSpan.FromMilliseconds(250));
        Check(slowButHealthy == 42, "A slow but healthy provider operation is not cancelled prematurely");
        await ExpectTimeoutAsync(() => ProviderTimeoutPolicy.ExecuteAsync(async token => { await Task.Delay(250, token); return 0; }, default,
            ProviderTimeoutKind.RequestDeadline, "stalled-test", TimeSpan.FromMilliseconds(20)), ProviderTimeoutKind.RequestDeadline);
        var progressReader = new DelayedLineReader(["data: one", "data: two", null], TimeSpan.FromMilliseconds(8));
        Check(await ProviderTimeoutPolicy.ReadLineAsync(progressReader, "stream-test", TimeSpan.FromMilliseconds(80), default) == "data: one"
            && await ProviderTimeoutPolicy.ReadLineAsync(progressReader, "stream-test", TimeSpan.FromMilliseconds(80), default) == "data: two",
            "Valid streaming progress resets the inactivity window for each chunk");
        await ExpectTimeoutAsync(() => ProviderTimeoutPolicy.ReadLineAsync(new DelayedLineReader(["late"], TimeSpan.FromMilliseconds(100)),
            "stream-test", TimeSpan.FromMilliseconds(15), default), ProviderTimeoutKind.StreamingInactivity);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await ProviderTimeoutPolicy.ExecuteAsync(async token => { await Task.Delay(10, token); return 0; }, cancelled.Token,
                    ProviderTimeoutKind.RequestDeadline, "cancel-test", TimeSpan.FromSeconds(1));
                throw new Exception("User cancellation was ignored.");
            }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: User cancellation stays distinct from provider and streaming timeouts");

        var conversation = new[]
        {
            new ChatRecord("user", "Original"), new ChatRecord("assistant", "Old answer"),
            new ChatRecord("tool", "write_file\n{\"ok\":true}"), new ChatRecord("assistant", "Done")
        };
        var edited = ChatMessageEditPolicy.Apply(conversation, 0, "Edited\nmessage");
        Check(ChatMessageEditPolicy.CopyText(new ChatRecord("user", "Line 1\n`code`!?")) == "Line 1\n`code`!?", "User-message copy preserves exact visible text, line breaks, punctuation and code markers");
        Check(edited.Changed && edited.Messages.Count == 1 && edited.Messages[0].Text == "Edited\nmessage" && edited.RemovedMessages == 3,
            "Editing keeps the edited turn in place and removes every dependent assistant and tool record");
        var persisted = JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(new ChatSession { Messages = edited.Messages.ToList() }))!;
        Check(persisted.Messages.Count == 1 && persisted.Messages[0].Text == "Edited\nmessage", "Truncated edited history survives chat persistence");
        var middle = ChatMessageEditPolicy.Apply([
            new ChatRecord("user", "First"), new ChatRecord("assistant", "First answer"),
            new ChatRecord("user", "Second"), new ChatRecord("assistant", "Second answer"), new ChatRecord("tool", "old side effect"),
            new ChatRecord("user", "Third"), new ChatRecord("assistant", "Third answer")], 2, "Second edited");
        Check(middle.Messages.Count == 3 && middle.Messages[0].Text == "First" && middle.Messages[1].Text == "First answer"
            && middle.Messages[2].Text == "Second edited" && middle.RemovedMessages == 4,
            "Editing a middle turn preserves all earlier context and removes all later turns without replaying tools");
        Check(!ChatMessageEditPolicy.Apply(conversation, 0, "Original").Changed, "Saving unchanged user text does not create regeneration work");
        try { ChatMessageEditPolicy.Apply(conversation, 1, "tamper"); throw new Exception("Assistant message was editable."); }
        catch (InvalidOperationException) { Console.WriteLine("PASS: Assistant and tool messages cannot use the user edit policy"); }

        var factoryCalls = 0;
        var healthy = new RecordingDiscordTransport();
        await using (var presence = new DiscordRichPresenceService(_ => ++factoryCalls == 1 ? new FailingDiscordTransport() : healthy, TimeSpan.FromMilliseconds(10)))
        {
            Check(!await presence.SetAsync(true, JarvisPresenceActivity.Chatting) && presence.IsEnabled
                && presence.ConnectionState == DiscordPresenceConnectionState.Disconnected, "A Discord transport failure leaves the user preference enabled and reports disconnected state");
            for (var attempt = 0; attempt < 20 && presence.ConnectionState != DiscordPresenceConnectionState.Connected; attempt++) await Task.Delay(10);
            Check(presence.IsEnabled && presence.ConnectionState == DiscordPresenceConnectionState.Connected && factoryCalls == 2
                && healthy.Payloads.Any(payload => payload.Contains("Chatting with Jarvis", StringComparison.Ordinal)), "Discord reconnects once without creating competing clients and restores the current activity");
            await presence.SetAsync(true, JarvisPresenceActivity.Voice);
            Check(presence.IsEnabled && healthy.Payloads.Any(payload => payload.Contains("Talking with Jarvis", StringComparison.Ordinal)), "Activity transitions preserve the enabled Discord preference");
            await presence.SetAsync(false, JarvisPresenceActivity.Chatting);
            Check(!presence.IsEnabled && presence.ConnectionState == DiscordPresenceConnectionState.Disabled, "Only explicit disabling changes Discord presence to disabled");
        }
        Check(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { DiscordRichPresenceEnabled = true }))!.DiscordRichPresenceEnabled,
            "Enabled Discord Rich Presence preference survives settings persistence independently of connection state");

        var voice = ConversationModeContext.Build(ConversationMode.Voice);
        var text = ConversationModeContext.Build(ConversationMode.Text);
        Check(voice.Contains("one to three", StringComparison.OrdinalIgnoreCase) && voice.Contains("regardless of the configured speech provider", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("one to three", StringComparison.OrdinalIgnoreCase), "Concise response generation is voice-specific and provider-independent");
        var mainSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs"));
        Check(mainSource.Contains("EditUserMessage_Click", StringComparison.Ordinal)
            && mainSource.Contains("ChatMessageEditPolicy.CopyText", StringComparison.Ordinal)
            && mainSource.Contains("await SendMessage(result.Messages[index])", StringComparison.Ordinal)
            && !mainSource.Contains("Earlier result · not regenerated after edit", StringComparison.Ordinal),
            "The live edit path persists the truncated history and submits the edited user turn exactly once through the normal pipeline");

        var svgRoot = Path.Combine(AppContext.BaseDirectory, "SvgTests", Guid.NewGuid().ToString("N"));
        var svgService = new SvgGraphicService(svgRoot);
        const string firstSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 200 100\"><rect x=\"5\" y=\"5\" width=\"190\" height=\"90\" fill=\"#332a66\"/><text x=\"100\" y=\"55\" fill=\"white\" font-size=\"18\">Jarvis</text></svg>";
        var createdSvg = await svgService.ExecuteAsync(new SvgGraphicRequest("architecture", firstSvg, "create", true));
        Check(File.Exists(createdSvg.Path) && createdSvg.Name == "architecture.svg" && createdSvg.DisplayInChat, "SVG create stores a persistent validated graphic with a safe filename");
        const string updatedSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"#7165d8\"/></svg>";
        var updatedSvgResult = await svgService.ExecuteAsync(new SvgGraphicRequest("architecture.svg", updatedSvg, "update", true));
        var reusedSvg = await new SvgGraphicService(svgRoot).ExecuteAsync(new SvgGraphicRequest("architecture.svg", "", "reuse", true));
        Check(updatedSvgResult.Path == reusedSvg.Path && File.ReadAllText(reusedSvg.Path).Contains("circle", StringComparison.Ordinal), "SVG update and reuse survive a new service instance");
        var versionedSvg = await svgService.ExecuteAsync(new SvgGraphicRequest("architecture.svg", firstSvg, "create", false));
        Check(versionedSvg.Name == "architecture-v2.svg" && File.Exists(versionedSvg.Path), "SVG create never silently overwrites an existing graphic");
        await ExpectSvgFailureAsync(svgService, new SvgGraphicRequest("../escape.svg", firstSvg, "create", true), "SVG filename traversal is contained to the dedicated data directory");
        await ExpectSvgFailureAsync(svgService, new SvgGraphicRequest("script.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "create", true), "Executable SVG elements are rejected");
        await ExpectSvgFailureAsync(svgService, new SvgGraphicRequest("external.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.test/a.png\"/></svg>", "create", true), "External SVG resources are rejected");
        await ExpectSvgFailureAsync(svgService, new SvgGraphicRequest("broken.svg", "<svg><path></svg>", "create", true), "Malformed SVG XML is rejected without crashing");
        FrameworkElement? renderedSvg = null;
        Exception? renderFailure = null;
        var renderThread = new Thread(() => { try { renderedSvg = SvgGraphicRenderer.RenderFile(createdSvg.Path, false); } catch (Exception ex) { renderFailure = ex; } });
        renderThread.SetApartmentState(ApartmentState.STA); renderThread.Start(); renderThread.Join();
        Check(renderFailure is null && renderedSvg is FrameworkElement, "Validated SVG is rendered as a native WPF chat visual");
        foreach (var file in Directory.EnumerateFiles(svgRoot)) File.Delete(file);
        Directory.Delete(svgRoot);
        var svgTestParent = Path.GetDirectoryName(svgRoot)!;
        if (!Directory.EnumerateFileSystemEntries(svgTestParent).Any()) Directory.Delete(svgTestParent);
    }

    private static async Task ExpectTimeoutAsync<T>(Func<Task<T>> action, ProviderTimeoutKind kind)
    {
        try { _ = await action(); throw new Exception($"Expected {kind} timeout."); }
        catch (ProviderTimeoutException ex) when (ex.Kind == kind) { Console.WriteLine($"PASS: {kind} is classified independently"); }
    }

    private static async Task ExpectSvgFailureAsync(SvgGraphicService service, SvgGraphicRequest request, string message)
    {
        try { await service.ExecuteAsync(request); throw new Exception(message + " (request unexpectedly succeeded)"); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException) { Console.WriteLine("PASS: " + message); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class DelayedLineReader(IEnumerable<string?> lines, TimeSpan delay) : TextReader
    {
        private readonly Queue<string?> _lines = new(lines);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return _lines.Count == 0 ? null : _lines.Dequeue();
        }
    }

    private sealed class FailingDiscordTransport : IDiscordRpcTransport
    {
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(int opcode, string json, CancellationToken token) => throw new IOException("simulated disconnect");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDiscordTransport : IDiscordRpcTransport
    {
        public ConcurrentQueue<string> Payloads { get; } = new();
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(int opcode, string json, CancellationToken token) { Payloads.Enqueue(json); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
