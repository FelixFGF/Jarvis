namespace Jarvis.Services;

public enum WorkspaceLayoutMode
{
    Hidden,
    Split,
    Files,
    Browser,
    Changes,
    Terminal
}

public static class WorkspaceLayoutState
{
    public static bool IsAvailable(string? root) => !string.IsNullOrWhiteSpace(root) && System.IO.Directory.Exists(root);
    public static WorkspaceLayoutMode Parse(string? value, bool visible)
    {
        if (!visible) return WorkspaceLayoutMode.Hidden;
        return Enum.TryParse<WorkspaceLayoutMode>(value, true, out var mode) && mode != WorkspaceLayoutMode.Hidden
            ? mode
            : WorkspaceLayoutMode.Split;
    }

    public static string WorkspaceView(WorkspaceLayoutMode mode) => mode switch
    {
        WorkspaceLayoutMode.Browser => "Browser",
        WorkspaceLayoutMode.Changes => "Changes",
        WorkspaceLayoutMode.Terminal => "Terminal",
        _ => "Files"
    };

    public static bool IsMaximized(WorkspaceLayoutMode mode) => mode is
        WorkspaceLayoutMode.Files or WorkspaceLayoutMode.Browser or WorkspaceLayoutMode.Changes or WorkspaceLayoutMode.Terminal;
}
