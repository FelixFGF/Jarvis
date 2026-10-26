namespace Jarvis.Services;

public static class TaskCompletionPolicy
{
    public static bool ShouldShow(int fileChangeCount, int meaningfulToolCount) => fileChangeCount > 0 || meaningfulToolCount > 0;
}
