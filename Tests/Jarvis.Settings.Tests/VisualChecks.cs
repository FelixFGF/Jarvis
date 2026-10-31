using Jarvis.Services;
using System.IO;

internal static class VisualChecks
{
    public static void Run()
    {
        var checks=0; void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS: "+name);checks++;}
        var charts = VisualResponseParser.Parse("""{"schemaVersion":1,"blocks":[{"type":"bar","title":"Bar","categories":["A","B"],"series":[{"name":"S","values":[-2,4]}]},{"type":"line","title":"Line","categories":["A","B"],"series":[{"name":"S","values":[1.5,2.5]}]},{"type":"pie","title":"Pie","series":[{"name":"S","values":[1,2]}]},{"type":"donut","title":"Donut","series":[{"name":"S","values":[1,2]}]}]}""");
        Check(charts.Blocks.Count==4 && charts.Blocks[0].Series[0].Values[0]==-2,"bar, line, pie and donut data including negative values");
        Check(VisualResponseRenderer.Render(charts, false) is System.Windows.Controls.ScrollViewer,"validated charts create native WPF visual components");
        var segments=VisualResponseParser.ParseSegments("Before\n```jarvis-visual\n{\"schemaVersion\":1,\"blocks\":[{\"type\":\"progress\",\"title\":\"Build\",\"value\":2,\"maximum\":4}]}\n```\nBetween\n```jarvis-visual\n{\"schemaVersion\":1,\"blocks\":[{\"type\":\"kpi\",\"title\":\"Stats\",\"metrics\":[{\"label\":\"Tests\",\"value\":42}]}]}\n```");
        Check(segments.Count(s=>s.Visual is not null)==2,"multiple visuals in one answer");
        Check(VisualResponseParser.ParseSegments("```jarvis-visual\n{bad}\n```").Single().Text!.Contains("⚠️"),"invalid JSON safely becomes visible text");
        foreach(var invalid in new[]{"{\"schemaVersion\":1,\"blocks\":[{\"type\":\"unknown\",\"title\":\"x\"}]}","{\"schemaVersion\":1,\"blocks\":[{\"type\":\"bar\",\"title\":\"x\",\"series\":[]}] }"}) { try{VisualResponseParser.Parse(invalid);throw new Exception("invalid visual accepted");}catch(InvalidDataException){} }
        Check(true,"unknown types and empty series rejected");
        var state=new RequestExecutionState(); Check(state.TryBeginTool("call-1")&&!state.TryBeginTool("call-1"),"duplicate tool action IDs blocked");
        Check(RequestExecutionState.Classify("",0,true,"stop")==ModelResponseKind.Empty && RequestExecutionState.MayRetry(ModelResponseKind.Empty,0,false),"empty response gets bounded pre-action retry");
        Check(!RequestExecutionState.MayRetry(ModelResponseKind.Truncated,0,true)&&RequestExecutionState.Classify("partial",0,true,"length")==ModelResponseKind.Truncated,"truncation preserves executed actions without replay");
        Check(RequestExecutionState.Classify(null,1,false,"tool_calls")==ModelResponseKind.InvalidToolCall,"invalid tool arguments classified explicitly");
        var emoji = "🚀🎨📊🧠🔍📈⚡🛠️💡✅"; Check(!emoji.Contains('\uFFFD') && emoji.EnumerateRunes().Count()>=10,"Unicode emoji remain valid scalar values");
        Check(EmojiTypography.IsEmojiElement("🧠") && EmojiTypography.IsEmojiElement("⚡") && EmojiTypography.IsEmojiElement("🎮") && EmojiTypography.IsEmojiElement("💾") && EmojiTypography.IsEmojiElement("👨‍💻") && !EmojiTypography.IsEmojiElement("Jarvis"), "simple and composed emoji graphemes are identified without changing ordinary text");
        var emojiText = new System.Windows.Controls.TextBlock(); EmojiTypography.Append(emojiText.Inlines, "Text 🧠 and 👨‍💻");
        var emojiRuns = emojiText.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
        Check(emojiRuns.Count(run => run.FontFamily?.Source == "Segoe UI Emoji") >= 2 && string.Concat(emojiRuns.Select(run => run.Text)) == "Text 🧠 and 👨‍💻", "visible emoji runs use Segoe UI Emoji while preserving copied text");
        Check(emojiRuns.Where(run => run.FontFamily?.Source == "Segoe UI Emoji").All(run => System.Windows.Media.TextOptions.GetTextFormattingMode(run) == System.Windows.Media.TextFormattingMode.Display), "emoji runs opt into the Windows color-font rendering path");
        Check(EmojiTypography.IsEmojiElement("❤️") && EmojiTypography.IsEmojiElement("👍🏽") && EmojiTypography.IsEmojiElement("🇩🇪"), "variation-selector, skin-tone and flag sequences remain single emoji text elements");
        var emojiRender = EmojiColorRendering.RenderVerificationImage();
        var emojiHarnessPath = Path.Combine(AppContext.BaseDirectory, "emoji-color-verification.png");
        File.WriteAllBytes(emojiHarnessPath, emojiRender.Png);
        Check(emojiRender.ColoredPixels > 250 && File.Exists(emojiHarnessPath), "Emoji.Wpf COLR/CPAL renderer produces real colored pixels for the dedicated chat harness");
        var greeting = RequestComplexityPolicy.Classify("hey was geht?", ThinkingMode.Deep);
        var normalQuestion = RequestComplexityPolicy.Classify("What is RAM?", ThinkingMode.Balanced);
        var complexTask = RequestComplexityPolicy.Classify("Analyze this project and fix the build.", ThinkingMode.Deep);
        Check(greeting.Level == RequestComplexityLevel.SimpleConversation && greeting.EffectiveThinkingMode == ThinkingMode.Fast && greeting.HistoryCharacterBudget <= 1800 && !greeting.IncludeRuntimeContext, "simple greeting selects the compact no-runtime fast path even when Deep is selected");
        Check(new[] { "Hey!", "Hallo Jarvis.", "Wie läuft's?", "Danke!", "Alles klar.", "Was machst du?", "Guten Morgen.", "Hi, wie geht es dir?" }
            .All(text => RequestComplexityPolicy.Classify(text, ThinkingMode.Deep).Level == RequestComplexityLevel.SimpleConversation),
            "representative German and English greetings consistently classify as Level 1");
        Check(normalQuestion.Level == RequestComplexityLevel.NormalQuestion && complexTask.Level == RequestComplexityLevel.ComplexTask, "normal questions and complex tasks remain distinct");
        var simpleTools = ChatRequestContextPolicy.SelectToolNames("hey was geht?");
        var simplePrompt = RequestComplexityPolicy.BuildSimpleSystemPrompt(true);
        Check(simpleTools.Count == 0 && !simplePrompt.Contains("SVG", StringComparison.OrdinalIgnoreCase) && !simplePrompt.Contains("Computer Use", StringComparison.OrdinalIgnoreCase)
            && RequestComplexityPolicy.EstimateInputTokens([simplePrompt, "hey was geht?"]) < 150,
            "simple outgoing request has no tools, SVG/Computer Use guidance, or thousand-token prompt overhead");
        var svgDecision = RequestComplexityPolicy.Classify("Create a professional SVG architecture diagram.", ThinkingMode.Deep);
        Check(svgDecision.Level == RequestComplexityLevel.ComplexTask && ChatRequestContextPolicy.SelectToolNames("Create a professional SVG architecture diagram.").Contains("create_or_update_svg"),
            "complex visual requests retain their dedicated tool and deep preference");
        Check(SvgDesignGuidance.Prompt.Contains("plan the composition", StringComparison.OrdinalIgnoreCase)
            && SvgDesignGuidance.Prompt.Contains("flowcharts", StringComparison.OrdinalIgnoreCase)
            && SvgDesignGuidance.Prompt.Contains("dark chat", StringComparison.OrdinalIgnoreCase)
            && SvgDesignGuidance.Prompt.Contains("review the complete SVG", StringComparison.OrdinalIgnoreCase),
            "runtime SVG guidance covers composition, layouts, dark UI contrast and final review");
        var rootSelection = JarvisStoragePaths.NormalizeSelectedInstallPath(Path.GetPathRoot(Path.GetTempPath())!);
        Check(rootSelection.EndsWith("Jarvis-FGF", StringComparison.OrdinalIgnoreCase) && JarvisStoragePaths.NormalizeSelectedInstallPath(rootSelection) == rootSelection, "root and custom install selections append Jarvis-FGF exactly once");
        var storageRoot = Path.Combine(AppContext.BaseDirectory, "JarvisStorageTests", Guid.NewGuid().ToString("N"));
        var legacyRoot = Path.Combine(AppContext.BaseDirectory, "JarvisLegacyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(legacyRoot, "SVG")); File.WriteAllText(Path.Combine(legacyRoot, "SVG", "kept.svg"), "<svg/>");
        var storage = new JarvisStoragePaths(storageRoot); storage.EnsureUsedDirectories();
        var migration = storage.MigrateLegacyData(legacyRoot);
        Check(migration.Succeeded && File.Exists(Path.Combine(storage.SvgRoot, "kept.svg")) && File.Exists(Path.Combine(legacyRoot, "SVG", "kept.svg")), "legacy SVG migration verifies the copy and preserves the source: " + string.Join("; ", migration.Errors));
        Check(EmojiReference.Prompt.Contains("Technology and AI") && EmojiReference.Prompt.Contains("🧠") && EmojiReference.Prompt.Contains("🔒"), "categorized Unicode emoji reference is available to protected context");
        Check(ToolPermissionPolicy.Parse("ask") == ToolPermissionMode.AlwaysAsk && ToolPermissionPolicy.Parse("automatic_review") == ToolPermissionMode.AutomaticReview && ToolPermissionPolicy.Parse("allow_eligible") == ToolPermissionMode.AllowEligible, "permission modes parse deterministically");
        Check(ToolPermissionPolicy.IsEligibleForAutomaticApproval("Read metadata", "Inspect active window", false) && !ToolPermissionPolicy.IsEligibleForAutomaticApproval("Delete file", "remove output", false) && !ToolPermissionPolicy.IsEligibleForAutomaticApproval("Read metadata", "safe", true), "automatic permission eligibility remains conservative");
        var adaptive=VisualResponseParser.Parse("""{"schemaVersion":1,"blocks":[{"type":"flowchart","title":"Flow","steps":[{"label":"SSD","detail":"Load"},{"label":"RAM","detail":"Hold"}]},{"type":"components","title":"Parts","steps":[{"label":"CPU","detail":"Compute"}]},{"type":"checklist","title":"Checks","steps":[{"label":"Build","status":"complete"}]}]}""");
        Check(adaptive.Blocks.Count==3 && VisualResponseRenderer.Render(adaptive,false) is System.Windows.Controls.ScrollViewer,"flowchart, component cards and checklist render as native WPF controls");
        var widths=VisualResponseRenderer.CalculateTableWidths(["Part","Detailed explanation","Value"],[["CPU","A substantially longer explanation that must wrap cleanly","12"],["RAM","Short","8"]]);
        Check(widths.Count==3 && widths[1]>widths[0] && widths.All(width=>width is >=105 and <=300),"table columns use bounded content-aware widths instead of equal narrow columns");
        Check(EmojiReference.Prompt.Split("VISUAL PRESENTATION AND EMOJI POLICY",StringSplitOptions.None).Length==2 && EmojiReference.Prompt.Contains("actual visualization structured output"),"visual and emoji policy is present exactly once");
        Console.WriteLine(checks+" visual/reliability checks passed.");
    }
}
