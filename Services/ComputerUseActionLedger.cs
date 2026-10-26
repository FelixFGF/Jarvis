namespace Jarvis.Services;

public enum ComputerUseActionStatus
{
    Pending,
    Executing,
    AwaitingObservation,
    Verifying,
    Succeeded,
    Failed,
    Cancelled,
    NeedsUserInput
}

/// <summary>
/// Keeps the authoritative execution/observation relationship for desktop actions.
/// A dispatched input is never success by itself: success requires a newer observation
/// explicitly associated with that action.
/// </summary>
public sealed class ComputerUseActionLedger
{
    private readonly List<ComputerUseActionRecord> _history = [];
    private string? _activeTaskId;
    public IReadOnlyList<ComputerUseActionRecord> History => _history;
    public string? ActiveTaskId => _activeTaskId;
    public ComputerUseActionRecord? Current => _activeTaskId is null
        ? _history.LastOrDefault()
        : _history.LastOrDefault(record => string.Equals(record.TaskId, _activeTaskId, StringComparison.Ordinal));
    public bool HasUnverifiedAction => Current?.Status is ComputerUseActionStatus.Pending
        or ComputerUseActionStatus.Executing or ComputerUseActionStatus.AwaitingObservation
        or ComputerUseActionStatus.Verifying or ComputerUseActionStatus.NeedsUserInput;

    /// <summary>
    /// Starts a fresh request scope. Results from a previous desktop task remain in the
    /// diagnostic history, but can no longer block or replace an answer to the new request.
    /// </summary>
    public void BeginTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("A task identifier is required.", nameof(taskId));
        _activeTaskId = taskId;
    }

    public void EndTask(string taskId)
    {
        if (string.Equals(_activeTaskId, taskId, StringComparison.Ordinal)) _activeTaskId = null;
    }

    public ComputerUseActionRecord Begin(string taskId, string? planId, int? planStep, string actionType,
        string target, string sourceObservationId, string intendedStateChange = "")
    {
        if (_activeTaskId is not null && !string.Equals(_activeTaskId, taskId, StringComparison.Ordinal))
            throw new InvalidOperationException("The desktop action does not belong to the active request.");
        if (HasUnverifiedAction) throw new InvalidOperationException("The preceding desktop action must be observed and resolved before another action can run.");
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(actionType)
            || string.IsNullOrWhiteSpace(sourceObservationId))
            throw new ArgumentException("A desktop action needs task, action, and source-observation identifiers.");
        var record = new ComputerUseActionRecord(Guid.NewGuid().ToString("N"), taskId, planId, planStep,
            actionType, target, sourceObservationId, intendedStateChange, DateTimeOffset.UtcNow);
        _history.Add(record);
        return record;
    }

    public void MarkExecuting(string actionId) => Transition(actionId, ComputerUseActionStatus.Pending, record =>
    {
        record.Status = ComputerUseActionStatus.Executing;
        record.StartedAtUtc = DateTimeOffset.UtcNow;
    });

    public void MarkDispatched(string actionId)
    {
        var record = FindCurrent(actionId);
        if (record.Status != ComputerUseActionStatus.AwaitingObservation)
            throw new InvalidOperationException("Input dispatch was not started for this action.");
        record.InputDispatched = true;
        record.ExecutionResult = "Input event dispatched; outcome not yet verified.";
        record.FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    public void MarkDispatchStarting(string actionId) => Transition(actionId, ComputerUseActionStatus.Executing, record =>
    {
        // A native input API can fail after partially dispatching input. Treat its
        // outcome as uncertain until a post-action observation is attached.
        record.Status = ComputerUseActionStatus.AwaitingObservation;
        record.InputMayHaveBeenDispatched = true;
    });

    public bool AttachObservation(string actionId, string observationId, string foregroundId, string evidenceReference)
    {
        var record = FindCurrent(actionId);
        if (record.Status is not (ComputerUseActionStatus.AwaitingObservation or ComputerUseActionStatus.Verifying or ComputerUseActionStatus.NeedsUserInput)
            || string.IsNullOrWhiteSpace(observationId) || observationId == record.SourceObservationId || observationId == record.ObservationId) return false;
        record.ObservationId = observationId;
        record.ObservedForegroundId = foregroundId;
        record.ObservationEvidenceReference = evidenceReference;
        record.ObservationAtUtc = DateTimeOffset.UtcNow;
        record.Status = ComputerUseActionStatus.Verifying;
        return true;
    }

    public bool Resolve(string actionId, string observationId, string outcome, string evidence,
        string currentForegroundId, string? planId = null, int? planStep = null)
    {
        var record = FindCurrent(actionId);
        if (record.Status != ComputerUseActionStatus.Verifying || record.ObservationId != observationId
            || string.IsNullOrWhiteSpace(evidence) || record.ObservedForegroundId != currentForegroundId
            || (planId is not null && (record.PlanId != planId || record.PlanStep != planStep))) return false;
        record.VerificationEvidence = evidence.Trim();
        record.ExecutionResult = outcome switch
        {
            "verified" => "Requested state verified against the associated observation.",
            "not_achieved" => "The associated observation did not show the requested state.",
            _ => "The associated observation was inconclusive."
        };
        record.Status = outcome switch
        {
            "verified" => ComputerUseActionStatus.Succeeded,
            "not_achieved" => ComputerUseActionStatus.Failed,
            "uncertain" => ComputerUseActionStatus.NeedsUserInput,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        record.VerifiedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public bool BelongsToPlanStep(string planId, int planStep) =>
        Current is { Status: ComputerUseActionStatus.Verifying } current
        && current.PlanId == planId && current.PlanStep == planStep;

    public bool ReferencesPlanStep(string planId, int planStep) =>
        Current is { } current && current.PlanId == planId && current.PlanStep == planStep;

    public bool IsVerifiedForPlanStep(string actionId, string observationId, string planId, int planStep) =>
        Current is { Status: ComputerUseActionStatus.Succeeded } current
        && current.ActionId == actionId && current.ObservationId == observationId
        && current.PlanId == planId && current.PlanStep == planStep;

    public void MarkFailed(string actionId, string error)
    {
        var record = FindCurrent(actionId);
        if (record.Status is ComputerUseActionStatus.Succeeded or ComputerUseActionStatus.Cancelled) return;
        record.Status = record.InputMayHaveBeenDispatched ? ComputerUseActionStatus.AwaitingObservation : ComputerUseActionStatus.Failed;
        record.Error = error;
        record.FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    public void MarkCancelled(string actionId, bool inputMayHaveBeenDispatched)
    {
        var record = FindCurrent(actionId);
        if (record.Status == ComputerUseActionStatus.Succeeded) return;
        record.Status = inputMayHaveBeenDispatched || record.InputDispatched || record.InputMayHaveBeenDispatched
            ? ComputerUseActionStatus.AwaitingObservation : ComputerUseActionStatus.Cancelled;
        record.Error = "Cancelled";
        record.FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    private ComputerUseActionRecord FindCurrent(string actionId) =>
        Current is { } record && record.ActionId == actionId
            ? record : throw new InvalidOperationException("The action is stale or does not belong to the current task state.");

    private void Transition(string actionId, ComputerUseActionStatus expected, Action<ComputerUseActionRecord> update)
    {
        var record = FindCurrent(actionId);
        if (record.Status != expected) throw new InvalidOperationException($"Invalid computer action transition: {record.Status} (expected {expected}).");
        update(record);
    }
}

public sealed class ComputerUseActionRecord(
    string actionId, string taskId, string? planId, int? planStep, string actionType,
    string target, string sourceObservationId, string intendedStateChange, DateTimeOffset createdAtUtc)
{
    public string ActionId { get; } = actionId;
    public string TaskId { get; } = taskId;
    public string? PlanId { get; } = planId;
    public int? PlanStep { get; } = planStep;
    public string ActionType { get; } = actionType;
    public string Target { get; } = target;
    public string SourceObservationId { get; } = sourceObservationId;
    public string IntendedStateChange { get; } = intendedStateChange;
    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
    public DateTimeOffset? StartedAtUtc { get; internal set; }
    public DateTimeOffset? FinishedAtUtc { get; internal set; }
    public DateTimeOffset? VerifiedAtUtc { get; internal set; }
    public DateTimeOffset? ObservationAtUtc { get; internal set; }
    public ComputerUseActionStatus Status { get; internal set; } = ComputerUseActionStatus.Pending;
    public bool InputDispatched { get; internal set; }
    public bool InputMayHaveBeenDispatched { get; internal set; }
    public string? ObservationId { get; internal set; }
    public string? ObservedForegroundId { get; internal set; }
    public string? ObservationEvidenceReference { get; internal set; }
    public string? VerificationEvidence { get; internal set; }
    public string? ExecutionResult { get; internal set; }
    public string? Error { get; internal set; }
}
