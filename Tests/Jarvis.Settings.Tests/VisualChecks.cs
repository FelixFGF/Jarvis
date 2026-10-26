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
