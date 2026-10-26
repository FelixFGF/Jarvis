using System.Text.Json;
using Jarvis.Models;
using Jarvis.Services;

internal static class AgentChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }

        Check(AgentExecutionOrchestrator.Assess("Wie spät ist es?").Kind == AgentTaskKind.SimpleAnswer
            && !AgentExecutionOrchestrator.Assess("Wie spät ist es?").RequiresPlan,
            "Simple questions stay on the direct-answer path");
        var coding = AgentExecutionOrchestrator.Assess(
            "Implementiere im bestehenden Projekt eine API, ergänze Tests und führe danach den Build aus.", hasWorkspace: true);
        Check(coding.Kind == AgentTaskKind.ComplexWork && coding.RequiresPlan && coding.RequiresTools,
            "Complex coding work requires a verified plan");
        var modelPlanContext = AgentExecutionOrchestrator.Start("Implementiere mehrere Änderungen und teste sie danach.", false, true).BuildRuntimeContext();
        Check(modelPlanContext.Contains("Create the visible plan_task", StringComparison.Ordinal)
            && !modelPlanContext.Contains("Inspect the project, versions, dependencies", StringComparison.Ordinal),
            "Controller requires a model plan without supplying a hardcoded developer checklist");
        Check(AgentExecutionOrchestrator.Assess("Recherchiere im Web die aktuelle offizielle API").Kind == AgentTaskKind.Research,
            "Explicit current research is classified deterministically");

        var run = AgentExecutionOrchestrator.Start("Erstelle und teste das Projekt", false, true);
        Check(run.TryBeginTool("call-1", "write_file", "{\"path\":\"a.cs\"}", out _), "First side-effecting tool call is accepted");
        Check(!run.TryBeginTool("call-2", "write_file", "{\"path\":\"a.cs\"}", out var duplicate)
            && duplicate.Contains("already attempted", StringComparison.OrdinalIgnoreCase),
            "Identical side effects are blocked across model rounds");
        Check(run.TryBeginTool("read-1", "read_file", "{\"path\":\"a.cs\"}", out _)
            && run.TryBeginTool("read-2", "read_file", "{\"path\":\"a.cs\"}", out _),
            "Safe observations may be repeated when verification needs them");
        Check(run.RecordFailure("compiler", "CS1000 at line 12") == AgentRetryDecision.RetryCorrected
            && run.RecordFailure("compiler", "CS1000 at line 33") == AgentRetryDecision.ChangeApproach
            && run.RecordFailure("compiler", "CS1000 at line 51") == AgentRetryDecision.Stop,
            "Repeated failure signatures produce bounded repair decisions");
        run.RecordToolResult("search_images", false, false, "provider timed out");
        Check(run.Phase == AgentExecutionPhase.Repairing && run.History.Last().Summary.Contains("timed out", StringComparison.Ordinal),
            "Failed tool calls retain their real failure instead of becoming user cancellation");
        Check(AgentExecutionOrchestrator.RequiresFollowUpVerification("run_powershell", "{\"ok\":true,\"state\":\"running\"}")
            && !AgentExecutionOrchestrator.RequiresFollowUpVerification("command_status", "{\"ok\":true,\"state\":\"completed\",\"exit_code\":0}"),
            "Command start is not completion while final command status is evidence");
        Check(!AgentExecutionOrchestrator.CanCompleteNonVisualPlanStep("In progress", true)
            && !AgentExecutionOrchestrator.CanCompleteNonVisualPlanStep("Needs confirmation", false)
            && AgentExecutionOrchestrator.CanCompleteNonVisualPlanStep("Needs confirmation", true),
            "Plan completion requires a separately reconciled successful tool result");

        var first = ChatGreetingService.SelectKey("de", deterministicIndex: 2);
        var second = ChatGreetingService.SelectKey("de", first, deterministicIndex: 2);
        Check(first != second, "Consecutive new chats avoid the same greeting");
        var personalized = ChatGreetingService.Resolve("de.0", "de", "FelixFGF");
        Check(personalized.Title.Contains("FelixFGF", StringComparison.Ordinal) && personalized.Title.StartsWith("Wie", StringComparison.Ordinal),
            "Verified Discord display name is naturally included where suitable");
        var chat = JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(new ChatSession { GreetingKey = second }))!;
        Check(chat.GreetingKey == second, "Greeting selection persists with the chat without changing its messages");

        Console.WriteLine(checks + " agent-core checks passed.");
    }
}
