namespace Jarvis.Services;

public enum RequestExecutionStage { Received, DecisionPending, ToolPrepared, ToolExecuted, ResultValidated, AnswerFinalized, Completed, Failed, Cancelled }
public enum ModelResponseKind { CompleteText, ToolCall, Empty, Truncated, InvalidToolCall }

public sealed class RequestExecutionState
{
    private readonly HashSet<string> _executedToolCalls = new(StringComparer.Ordinal);
    public RequestExecutionStage Stage { get; private set; } = RequestExecutionStage.Received;
    public void MoveTo(RequestExecutionStage next)
    {
        if (Stage is RequestExecutionStage.Completed or RequestExecutionStage.Cancelled) return;
        Stage = next;
    }
    public bool TryBeginTool(string id) => !string.IsNullOrWhiteSpace(id) && _executedToolCalls.Add(id);
    public static ModelResponseKind Classify(string? content, int toolCount, bool toolArgumentsValid, string? finishReason) =>
        toolCount > 0 ? toolArgumentsValid ? ModelResponseKind.ToolCall : ModelResponseKind.InvalidToolCall :
        string.IsNullOrWhiteSpace(content) ? finishReason is "length" or "content_filter" ? ModelResponseKind.Truncated : ModelResponseKind.Empty :
        finishReason == "length" ? ModelResponseKind.Truncated : ModelResponseKind.CompleteText;
    public static bool MayRetry(ModelResponseKind kind, int attempt, bool anyToolExecuted) => !anyToolExecuted && attempt < 2 && kind is ModelResponseKind.Empty or ModelResponseKind.Truncated;
}
