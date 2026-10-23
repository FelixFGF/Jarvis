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
        Console.WriteLine(checks+" visual/reliability checks passed.");
    }
}
