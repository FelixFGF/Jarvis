namespace Jarvis.Services;

/// <summary>Observable lifecycle states for a single, cancellable desktop-control session.</summary>
public enum ComputerUseSessionState
{
    Stopped,
    Starting,
    AwaitingApproval,
    Active,
    Paused,
    Stopping,
    Finished,
    Failed
}

public static class ComputerUseSessionStateExtensions
{
    public static bool IsRunning(this ComputerUseSessionState state) =>
        state is ComputerUseSessionState.Starting or ComputerUseSessionState.AwaitingApproval
            or ComputerUseSessionState.Active or ComputerUseSessionState.Paused or ComputerUseSessionState.Stopping;
}
