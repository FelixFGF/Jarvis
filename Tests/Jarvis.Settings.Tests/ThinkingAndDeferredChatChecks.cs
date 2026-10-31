using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Models;
using Jarvis.Services;

internal static class ThinkingAndDeferredChatChecks
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS: " + name);
            checks++;
        }

        using var inertClient = new HttpClient(new StaticHandler(HttpStatusCode.OK, "{}"));
        foreach (var (mode, expected) in new[]
        {
            (ThinkingMode.Fast, "low"),
            (ThinkingMode.Balanced, "medium"),
            (ThinkingMode.Deep, "high")
        })
        {
            var control = await ThinkingModeService.ResolveAsync(inertClient,
                "https://api.openai.com/v1", "gpt-5.2", mode, null, CancellationToken.None);
            var payload = ThinkingModeService.Apply(new JsonObject { ["model"] = "gpt-5.2" }, control);
            Check(control.IsApplied && payload["reasoning_effort"]?.GetValue<string>() == expected,
                $"OpenAI {mode} maps to verified reasoning_effort={expected}");
        }

        var generic = await ThinkingModeService.ResolveAsync(inertClient,
            "https://models.example.test/v1", "gpt-5.2", ThinkingMode.Deep, null, CancellationToken.None);
        var unchanged = ThinkingModeService.Apply(new JsonObject { ["model"] = "gpt-5.2" }, generic);
        Check(!generic.IsApplied && unchanged["reasoning_effort"] is null,
            "Unverified OpenAI-compatible endpoints receive no unsupported reasoning parameter");

        var conversationalTools = ChatRequestContextPolicy.SelectToolNames("Hi");
        Check(conversationalTools.Count == 0,
            "Ordinary LM Studio conversation does not attach the full Jarvis tool catalog");
        var fileTools = ChatRequestContextPolicy.SelectToolNames("Please inspect this project file and fix the code");
        Check(fileTools.Contains("read_file") && fileTools.Contains("write_file") && fileTools.Contains("plan_task")
            && !fileTools.Contains("computer_click") && !fileTools.Contains("spotify_control"),
            "Project tasks receive a narrow relevant tool set without unrelated schemas");
        var monitorTools = ChatRequestContextPolicy.SelectToolNames("Analyze both monitors without Computer Use");
        Check(monitorTools.Contains("analyze_monitors") && !monitorTools.Contains("computer_click"),
            "Monitor analysis keeps its dedicated vision tool and respects an explicit Computer Use exclusion");
        var svgTools = ChatRequestContextPolicy.SelectToolNames("Create an SVG architecture diagram and show it in chat");
        Check(svgTools.Contains("create_or_update_svg") && !svgTools.Contains("computer_click"),
            "Explicit visual-diagram requests receive the dedicated SVG tool without Computer Use");
        Check(ChatRequestContextPolicy.IsContextLimitFailure("request (13962 tokens) exceeds the available context size (8192 tokens)")
            && !ChatRequestContextPolicy.IsContextLimitFailure("temporary network failure"),
            "LM Studio context-limit errors are identified without misclassifying transport failures");
        var conversationalPayload = JsonSerializer.Serialize(ApiModelCatalogService.CreateChatCompletionPayload(
            "google/gemma-4-12b-qat", new[] { new { role = "user", content = "Hi" } }, null, null, 0.4));
        Check(!conversationalPayload.Contains("\"tools\"", StringComparison.Ordinal)
            && !conversationalPayload.Contains("\"tool_choice\"", StringComparison.Ordinal),
            "OpenAI-compatible conversational payload omits unsupported and unnecessary tool fields");
        var lmStudioHandler = new StaticHandler(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello!\"},\"finish_reason\":\"stop\"}]}");
        using (var lmStudioClient = new HttpClient(lmStudioHandler))
        using (var lmStudioResponse = await OpenAiCompatibleChatTransport.SendAsync(lmStudioClient,
            "http://lmstudio.test:1234/v1", null, conversationalPayload, CancellationToken.None))
        {
            var responseBody = await lmStudioResponse.Content.ReadAsStringAsync();
            Check(lmStudioResponse.IsSuccessStatusCode
                && lmStudioHandler.LastPath == "/v1/chat/completions"
                && lmStudioHandler.LastBody?.Contains("\"content\":\"Hi\"", StringComparison.Ordinal) == true
                && responseBody.Contains("Hello!", StringComparison.Ordinal),
                "Simple Hi completes through an OpenAI-compatible LM Studio transport fixture");
        }

        var ollamaHandler = new StaticHandler(HttpStatusCode.OK,
            "{\"thinking\":{\"values\":[\"low\",\"medium\",\"high\"],\"default\":\"medium\"}}");
        using var ollamaClient = new HttpClient(ollamaHandler);
        var ollama = await ThinkingModeService.ResolveAsync(ollamaClient,
            "http://localhost:11434/v1", "qwen3:8b", ThinkingMode.Deep, null, CancellationToken.None);
        Check(ollama.IsApplied && ollama.ReasoningEffort == "high" && ollamaHandler.LastPath == "/api/show"
            && ollamaHandler.LastBody?.Contains("qwen3:8b", StringComparison.Ordinal) == true,
            "Ollama model metadata is queried and Deep uses an advertised level");

        using var noThinkingClient = new HttpClient(new StaticHandler(HttpStatusCode.OK, "{\"capabilities\":[\"completion\"]}"));
        var noThinking = await ThinkingModeService.ResolveAsync(noThinkingClient,
            "http://localhost:11434/v1", "plain-model", ThinkingMode.Deep, null, CancellationToken.None);
        Check(!noThinking.IsApplied, "Ollama model without advertised thinking remains compatible and unchanged");

        using var booleanThinkingClient = new HttpClient(new StaticHandler(HttpStatusCode.OK, "{\"thinking\":{\"values\":[false,true]}}"));
        var booleanThinking = await ThinkingModeService.ResolveAsync(booleanThinkingClient,
            "http://localhost:11434/v1", "boolean-think-model", ThinkingMode.Deep, null, CancellationToken.None);
        Check(!booleanThinking.IsApplied,
            "Ollama boolean-only native thinking is not misrepresented as OpenAI reasoning_effort");

        var fastUi = ThinkingModeService.Present(ThinkingMode.Fast, german: false);
        var balancedUi = ThinkingModeService.Present(ThinkingMode.Balanced, german: false);
        var deepUi = ThinkingModeService.Present(ThinkingMode.Deep, german: false);
        var germanDeepUi = ThinkingModeService.Present(ThinkingMode.Deep, german: true);
        Check(fastUi.Icon == "⚡" && fastUi.Label == "Fast"
            && balancedUi.Icon == "⚖" && balancedUi.Label == "Balanced"
            && deepUi.Icon == "🧠" && deepUi.Label == "Deep"
            && germanDeepUi.Label == "Tief",
            "Thinking modes expose distinct immediate English and German button presentations");

        var deferred = new DeferredConversationState();
        var first = deferred.Begin("model-a", "hello");
        var repeated = deferred.Begin("model-b", "other");
        Check(ReferenceEquals(first, repeated) && deferred.IsActive,
            "Repeated New Chat clicks reuse one temporary conversation");
        Check(!deferred.TryMaterialize("   ", out _) && deferred.Pending is not null,
            "Whitespace does not create a persisted conversation");
        deferred.Reset();
        Check(!deferred.IsActive && deferred.Pending is null,
            "Switching to an existing chat discards the temporary conversation without history");
        var restored = deferred.Begin("model-c", "new");
        Check(!ReferenceEquals(first, restored), "A later New Chat starts a fresh temporary conversation");
        Check(deferred.TryMaterialize("hello", out ChatSession? materialized)
            && ReferenceEquals(restored, materialized) && deferred.Pending is null && !deferred.IsActive,
            "First meaningful message materializes the temporary chat exactly once");
        Check(!deferred.TryMaterialize("duplicate", out _),
            "A retry cannot materialize or duplicate the same conversation again");
        deferred.Begin("model-a", "hello");
        deferred.Reset();
        Check(deferred.Pending is null && !deferred.IsActive,
            "Cancelled voice or empty temporary chat can be discarded without history");

        var active = new ChatSession { Title = "Active" };
        var inactive = new ChatSession { Title = "Inactive" };
        var third = new ChatSession { Title = "Third" };
        var chats = new[] { active, inactive, third };
        Check(ChatDeletionPolicy.Create(chats, chats, active, inactive, confirmed: false) is null,
            "Cancel and Escape confirmation results cannot create a deletion plan");
        var inactivePlan = ChatDeletionPolicy.Create(chats, chats, active, inactive, confirmed: true)!;
        Check(inactivePlan.RemainingChats.Count == 2
            && inactivePlan.RemainingChats.All(chat => chat.Id != inactive.Id)
            && inactivePlan.NextActiveChat?.Id == active.Id
            && !inactivePlan.DeletesActiveChat,
            "Confirming inactive-chat deletion removes only the target and retains the active chat");
        var activePlan = ChatDeletionPolicy.Create(chats, chats, active, active, confirmed: true)!;
        Check(activePlan.DeletesActiveChat && activePlan.NextActiveChat?.Id == inactive.Id,
            "Deleting the active chat deterministically selects the next open conversation");
        var lastPlan = ChatDeletionPolicy.Create(new[] { active }, new[] { active }, active, active, confirmed: true)!;
        Check(lastPlan.RemainingChats.Count == 0 && lastPlan.NextActiveChat is null,
            "Deleting the final chat leaves empty history for the deferred New Chat state");

        var mainXaml = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml")));
        var mainSource = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs")));
        Check(mainXaml.Contains("x:Name=\"ThinkingModeIcon\"", StringComparison.Ordinal)
            && mainXaml.Contains("x:Name=\"ThinkingModeLabel\"", StringComparison.Ordinal)
            && mainSource.Contains("ThinkingButton.UpdateLayout()", StringComparison.Ordinal)
            && mainSource.Contains("Text = isSelected ? \"✓\"", StringComparison.Ordinal),
            "Active Thinking icon, label and explicit menu checkmark are wired into the live composer");
        Check(!mainSource.Contains("ChatList.SelectedItem = chat;\n        ShowChatContextMenu", StringComparison.Ordinal)
            && mainSource.Contains("new ChatDeleteConfirmationDialog", StringComparison.Ordinal)
            && mainSource.Contains("ChatDeletionPolicy.Create", StringComparison.Ordinal),
            "Right-click deletion no longer activates inactive chats and uses the dedicated confirmation policy");
        Check(mainSource.Contains("contextLimitRetryAttempted", StringComparison.Ordinal)
            && mainSource.Contains("Math.Min(4_000, complexity.HistoryCharacterBudget)", StringComparison.Ordinal)
            && mainSource.Contains("BuildCompactSystemContext", StringComparison.Ordinal),
            "Context-limit recovery is bounded to one compact retry instead of leaving an endless loading state");
        Check(mainSource.Contains("complexity.Level == RequestComplexityLevel.SimpleConversation", StringComparison.Ordinal)
            && mainSource.Contains("simpleFastPath ? null", StringComparison.Ordinal)
            && mainSource.Contains("complexity.EffectiveThinkingMode", StringComparison.Ordinal),
            "Live request path skips tool selection/fallback routing and forces effective Fast reasoning for simple conversation");

        Console.WriteLine($"{checks} thinking/deferred-chat checks passed.");
    }

    private sealed class StaticHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
