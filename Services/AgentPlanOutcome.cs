namespace Jarvis.Services;

/// <summary>Pure status rules shared by the agent plan UI and regression tests.</summary>
public static class AgentPlanOutcome
{
    public static string ToolAttemptStatus(bool succeeded, bool outcomeVerified)
    {
        if (!succeeded) return "Pending"; // A failed method is recoverable; the goal may still be achieved another way.
        return outcomeVerified ? "Completed" : "Needs confirmation";
    }

    public static string ResolveCancellation(string currentTaskStatus, bool userCancelled, bool responseAlreadyDelivered = false)
    {
        if (responseAlreadyDelivered || string.Equals(currentTaskStatus, "Completed", StringComparison.Ordinal)) return "Completed";
        if (!userCancelled) return currentTaskStatus;
        return "Cancelled";
    }

    public static bool CanConfirmComputerOutcome(string stepStatus, bool observationIsFresh, bool foregroundMatches) =>
        stepStatus == "Needs confirmation" && observationIsFresh && foregroundMatches;

    public static bool CanCompleteComputerStep(string stepStatus, bool hasLinkedAction,
        bool linkedActionVerified, bool exactFreshObservationMatches) =>
        stepStatus == "Needs confirmation"
        && (hasLinkedAction ? linkedActionVerified : exactFreshObservationMatches);

    public static string ResolvePlanStatus(IEnumerable<string> stepStatuses)
    {
        var statuses = stepStatuses.ToArray();
        if (statuses.Length > 0 && statuses.All(status => status == "Completed")) return "Completed";
        if (statuses.Any(status => status is "Pending" or "In progress")) return "Incomplete";
        if (statuses.Any(status => status == "Needs confirmation")) return "Needs confirmation";
        if (statuses.Any(status => status is "Completed" or "Partially completed")) return "Partially completed";
        if (statuses.Any(status => status == "Cancelled")) return "Cancelled";
        if (statuses.Any(status => status == "Failed")) return "Failed";
        return "Incomplete";
    }
}
