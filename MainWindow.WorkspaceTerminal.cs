using Jarvis.Services;
using System.IO;
using System.Windows;

namespace Jarvis;

public partial class MainWindow
{
    private readonly TerminalSessionService _terminalSessions = new();
    private WorkspaceLayoutMode _workspaceLayout = WorkspaceLayoutMode.Hidden;
    private JarvisPresenceActivity _workspacePresenceActivity = JarvisPresenceActivity.Chatting;

    private void ConfigureWorkspaceAndTerminals()
    {
        IntegratedWorkspace.CollapseRequested += (_, _) => SetWorkspaceLayout(WorkspaceLayoutMode.Hidden);
        IntegratedWorkspace.SplitRequested += (_, _) => SetWorkspaceLayout(WorkspaceLayoutMode.Split);
        IntegratedWorkspace.MaximizeRequested += (_, view) => SetWorkspaceLayout(ParseWorkspaceView(view));
        IntegratedWorkspace.ProjectRootChanged += (_, root) => OnProjectRootChanged(root);
        IntegratedWorkspace.ViewChanged += (_, view) =>
        {
            _workspacePresenceActivity = view.Equals("Files", StringComparison.OrdinalIgnoreCase) || view.Equals("Terminal", StringComparison.OrdinalIgnoreCase)
                ? JarvisPresenceActivity.Coding : JarvisPresenceActivity.Chatting;
            _ = UpdateDiscordPresenceAsync(_voiceModeEnabled ? JarvisPresenceActivity.Voice : _workspacePresenceActivity);
        };
        ChatTerminalPanel.Configure(_terminalSessions, TerminalAllowedRoots, DefaultTerminalDirectory, AuthorizeTerminalCommandAsync, _config.UiLanguage);
        IntegratedWorkspace.ConfigureTerminal(_terminalSessions, TerminalAllowedRoots, DefaultTerminalDirectory, AuthorizeTerminalCommandAsync, _config.UiLanguage);
        _workspaceLayout = WorkspaceLayoutState.Parse(_config.WorkspaceLayoutMode, _config.WorkspaceVisible);
        SetWorkspaceLayout(_workspaceLayout, false);
        SetChatTerminalVisibility(_config.ChatTerminalVisible, false);
        UpdateWorkspaceAvailability();
    }

    private static WorkspaceLayoutMode ParseWorkspaceView(string view) => view.ToLowerInvariant() switch
    {
        "browser" => WorkspaceLayoutMode.Browser,
        "changes" => WorkspaceLayoutMode.Changes,
        "terminal" => WorkspaceLayoutMode.Terminal,
        _ => WorkspaceLayoutMode.Files
    };

    private void SetWorkspaceLayout(WorkspaceLayoutMode mode, bool persist = true)
    {
        _workspaceLayout = mode;
        var hidden = mode == WorkspaceLayoutMode.Hidden;
        var maximized = WorkspaceLayoutState.IsMaximized(mode);
        IntegratedWorkspace.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        IntegratedWorkspaceSplitter.Visibility = mode == WorkspaceLayoutMode.Split ? Visibility.Visible : Visibility.Collapsed;
        ChatColumn.Width = hidden ? new GridLength(1, GridUnitType.Star) : maximized ? new GridLength(0) : new GridLength(5, GridUnitType.Star);
        WorkspaceSplitterColumn.Width = mode == WorkspaceLayoutMode.Split ? new GridLength(7) : new GridLength(0);
        IntegratedWorkspaceColumn.Width = hidden ? new GridLength(0) : maximized ? new GridLength(1, GridUnitType.Star) : new GridLength(6, GridUnitType.Star);
        WorkspaceReopenButton.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceToggleButton.Content = hidden
            ? (_config.UiLanguage == "de" ? "▣ Arbeitsbereich öffnen" : "▣ Open workspace")
            : (_config.UiLanguage == "de" ? "▣ Arbeitsbereich schließen" : "▣ Close workspace");
        if (WorkspaceLayoutState.IsMaximized(mode)) IntegratedWorkspace.ShowView(WorkspaceLayoutState.WorkspaceView(mode));
        _config.WorkspaceVisible = !hidden;
        _config.WorkspaceLayoutMode = mode.ToString();
        if (persist && !_loading) _ = PersistConfig();
    }

    private void WorkspaceToggle_Click(object sender, RoutedEventArgs e) => SetWorkspaceLayout(
        _workspaceLayout == WorkspaceLayoutMode.Hidden ? WorkspaceLayoutMode.Split : WorkspaceLayoutMode.Hidden);

    private void ChatTerminalToggle_Click(object sender, RoutedEventArgs e) => SetChatTerminalVisibility(ChatTerminalContainer.Visibility != Visibility.Visible);

    private void SetChatTerminalVisibility(bool visible, bool persist = true)
    {
        ChatTerminalContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ChatTerminalRow.Height = visible ? new GridLength(300) : new GridLength(0);
        ChatTerminalToggleButton.Content = visible
            ? (_config.UiLanguage == "de" ? "⌁ Terminal schließen" : "⌁ Close terminal")
            : "⌁ Terminal";
        _config.ChatTerminalVisible = visible;
        if (visible) ChatTerminalPanel.EnsureSession();
        if (persist && !_loading) _ = PersistConfig();
    }

    private IReadOnlyCollection<string> TerminalAllowedRoots()
    {
        var roots = (_config.AllowedRoots ?? []).Where(Directory.Exists).Select(Path.GetFullPath).ToList();
        if (IntegratedWorkspace.ProjectRoot is { } project && Directory.Exists(project) && !roots.Contains(project, StringComparer.OrdinalIgnoreCase)) roots.Add(project);
        if (_activeChat?.Workspace is { Length: > 0 } chatRoot && Directory.Exists(chatRoot) && !roots.Contains(chatRoot, StringComparer.OrdinalIgnoreCase)) roots.Add(chatRoot);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(profile) && !roots.Contains(profile, StringComparer.OrdinalIgnoreCase)) roots.Add(profile);
        return roots;
    }

    private string DefaultTerminalDirectory() => IntegratedWorkspace.ProjectRoot is { } root && Directory.Exists(root)
        ? root
        : _activeChat?.Workspace is { } workspace && Directory.Exists(workspace)
            ? workspace
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private async Task<bool> AuthorizeTerminalCommandAsync(string shell, string command, string workingDirectory, CancellationToken token)
    {
        var id = "terminal-ui-" + Guid.NewGuid().ToString("N");
        var input = $"{shell} · {workingDirectory}\n{command}";
        BeginToolActivity(id, shell == "cmd" ? "run_cmd" : "run_powershell", input);
        var german = _config.UiLanguage == "de";
        var approval = await RequestApprovalInlineAsync(german ? "Terminalbefehl ausführen?" : "Run terminal command?",
            $"{shell}\n{workingDirectory}\n\n{command}", token, $"terminal:{shell}:{workingDirectory}:{command}");
        CompleteToolActivity(id, approval.Approved, approval.Approved ? (german ? "Freigegeben; Ausgabe erscheint im Terminal." : "Approved; output is shown in the terminal.") : approval.Feedback);
        return approval.Approved;
    }

    private void OnProjectRootChanged(string? root)
    {
        if (_activeChat is not null) _activeChat.Workspace = root ?? "";
        WorkspaceLabel.Text = string.IsNullOrWhiteSpace(root)
            ? (_config.UiLanguage == "de" ? "Kein Ordner ausgewählt" : "No folder selected")
            : root;
        if (!string.IsNullOrWhiteSpace(root) && !_config.RecentWorkspaces.Contains(root, StringComparer.OrdinalIgnoreCase)) _config.RecentWorkspaces.Insert(0, root);
        if (!string.IsNullOrWhiteSpace(root) && !_config.AllowedRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) _config.AllowedRoots.Add(root);
        ToolRootsList.ItemsSource = null; ToolRootsList.ItemsSource = _config.AllowedRoots;
        UpdateWorkspaceAvailability(root);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        _ = PersistConfig(); _ = SaveChat();
    }

    private void UpdateWorkspaceAvailability(string? selectedRoot = null)
    {
        var root = selectedRoot ?? IntegratedWorkspace.ProjectRoot ?? _activeChat?.Workspace;
        var available = WorkspaceLayoutState.IsAvailable(root);
        WorkspaceToggleButton.IsEnabled = available;
        WorkspaceReopenButton.IsEnabled = available;
        ChatTerminalToggleButton.IsEnabled = available;
        var hint = _config.UiLanguage == "de" ? "Wähle zuerst einen Projektordner für diesen Chat." : "Choose a project folder for this chat first.";
        WorkspaceToggleButton.ToolTip = available ? null : hint;
        WorkspaceReopenButton.ToolTip = available ? null : hint;
        ChatTerminalToggleButton.ToolTip = available ? null : hint;
        if (!available)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Hidden, false);
            SetChatTerminalVisibility(false, false);
        }
    }

    private void ApplyWorkspaceTerminalLanguage()
    {
        WorkspaceReopenButton.Content = _config.UiLanguage == "de" ? "Arbeitsbereich öffnen" : "Open workspace";
        ChatTerminalPanel.ApplyLanguage(_config.UiLanguage);
        SetWorkspaceLayout(_workspaceLayout, false);
        SetChatTerminalVisibility(_config.ChatTerminalVisible, false);
        UpdateWorkspaceAvailability();
    }
}
