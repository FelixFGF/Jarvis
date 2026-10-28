using Jarvis.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Jarvis;

public partial class TerminalPanel : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<TerminalSessionSnapshot> _sessions = [];
    private TerminalSessionService? _service;
    private Func<IReadOnlyCollection<string>>? _allowedRoots;
    private Func<string>? _defaultWorkingDirectory;
    private Func<string, string, string, CancellationToken, Task<bool>>? _authorize;
    private string _language = "en";
    private string? _selectedSessionId;

    public TerminalPanel()
    {
        InitializeComponent();
        SessionTabs.ItemsSource = _sessions;
    }

    public void Configure(TerminalSessionService service, Func<IReadOnlyCollection<string>> allowedRoots,
        Func<string> defaultWorkingDirectory, Func<string, string, string, CancellationToken, Task<bool>> authorize, string language)
    {
        if (_service is not null) _service.SessionChanged -= Service_SessionChanged;
        _service = service;
        _allowedRoots = allowedRoots;
        _defaultWorkingDirectory = defaultWorkingDirectory;
        _authorize = authorize;
        _service.SessionChanged += Service_SessionChanged;
        ApplyLanguage(language);
        RefreshSessions();
    }

    public void ApplyLanguage(string language)
    {
        _language = language == "de" ? "de" : "en";
        var de = _language == "de";
        ClearButton.Content = de ? "Leeren" : "Clear";
        ClearButton.ToolTip = de ? "Nur die sichtbare Ausgabe leeren" : "Clear visible output only";
        CloseButton.ToolTip = de ? "Aktiven Terminal-Tab schließen" : "Close active terminal tab";
        NewPowerShellButton.ToolTip = de ? "Neuen Terminal-Tab auswählen" : "Choose a new terminal tab";
        NewTerminalMenuButton.ToolTip = de ? "Terminalprofil auswählen" : "Choose terminal profile";
        NewTabHeading.Text = de ? "Neuer Terminal-Tab" : "New terminal tab";
        PopupCmdButton.Content = de ? "⌨  Eingabeaufforderung (CMD)" : "⌨  Command Prompt (CMD)";
        CancelButton.Content = de ? "Stopp" : "Stop";
        RefreshSelected();
    }

    public bool EnsureSession()
    {
        if (_sessions.Any(item => item.State is "ready" or "running")) return true;
        return StartSession("powershell");
    }

    private bool StartSession(string shell)
    {
        if (_service is null || _allowedRoots is null) return false;
        try
        {
            var cwd = _defaultWorkingDirectory?.Invoke();
            if (string.IsNullOrWhiteSpace(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var session = _service.Start(shell, cwd, _allowedRoots());
            _selectedSessionId = session.Id;
            RefreshSessions();
            SessionTabs.SelectedValue = session.Id;
            Dispatcher.BeginInvoke(() => { CommandBox.Focus(); Keyboard.Focus(CommandBox); });
            return true;
        }
        catch (Exception ex)
        {
            TerminalState.Text = ex.Message;
            return false;
        }
    }

    private void Service_SessionChanged(object? sender, string id) => Dispatcher.BeginInvoke(() =>
    {
        var selected = _selectedSessionId ?? SessionTabs.SelectedValue as string;
        RefreshSessions();
        if (_sessions.Any(item => item.Id == id)) SessionTabs.SelectedValue = selected ?? id;
    });

    private void RefreshSessions()
    {
        var selected = _selectedSessionId ?? SessionTabs.SelectedValue as string;
        _sessions.Clear();
        if (_service is not null)
            foreach (var session in _service.Snapshot().Where(item => item.State != "closed")) _sessions.Add(session);
        if (selected is not null && _sessions.Any(item => item.Id == selected)) SessionTabs.SelectedValue = selected;
        else if (_sessions.Count > 0) { SessionTabs.SelectedIndex = 0; _selectedSessionId = _sessions[0].Id; }
        else _selectedSessionId = null;
        RefreshSelected();
    }

    private TerminalSessionSnapshot? SelectedSession()
    {
        if (SessionTabs.SelectedItem is not TerminalSessionSnapshot selected) return null;
        try { return _service?.Get(selected.Id) ?? selected; }
        catch { return selected; }
    }

    private void RefreshSelected()
    {
        var session = SelectedSession();
        if (session is null)
        {
            OutputBox.Text = ErrorBox.Text = "";
            CommandBox.IsEnabled = CancelButton.IsEnabled = CloseButton.IsEnabled = ClearButton.IsEnabled = false;
            CancelButton.Visibility = ErrorContainer.Visibility = Visibility.Collapsed;
            PromptLabel.Text = "PS>";
            WorkingDirectoryLabel.Text = "";
            TerminalState.Text = _language == "de" ? "Kein Terminal geöffnet. Mit ＋ startet PowerShell." : "No terminal is open. Use ＋ to start PowerShell.";
            return;
        }
        OutputBox.Text = session.StandardOutput;
        ErrorBox.Text = session.StandardError;
        OutputBox.ScrollToEnd();
        ErrorBox.ScrollToEnd();
        CommandBox.IsEnabled = session.State == "ready";
        CancelButton.IsEnabled = session.State == "running";
        CloseButton.IsEnabled = ClearButton.IsEnabled = true;
        CancelButton.Visibility = session.State == "running" ? Visibility.Visible : Visibility.Collapsed;
        ErrorContainer.Visibility = string.IsNullOrWhiteSpace(session.StandardError) ? Visibility.Collapsed : Visibility.Visible;
        WorkingDirectoryLabel.Text = session.WorkingDirectory;
        PromptLabel.Text = session.Shell == "cmd" ? $"{session.WorkingDirectory}>" : $"PS {session.WorkingDirectory}>";
        TerminalState.Text = $"{DisplayShell(session.Shell)} · {LocalizedState(session.State)}" +
            (session.LastExitCode is int code ? $" · Exit {code}" : "");
    }

    private static string DisplayShell(string shell) => shell == "cmd" ? "CMD" : "PowerShell";
    private string LocalizedState(string state) => (_language, state) switch
    {
        ("de", "ready") => "Bereit", ("de", "running") => "Befehl läuft", ("de", "failed") => "Fehlgeschlagen",
        ("de", "cancelled") => "Abgebrochen", ("de", "closed") => "Geschlossen",
        (_, "ready") => "Ready", (_, "running") => "Command running", (_, "failed") => "Failed",
        (_, "cancelled") => "Cancelled", (_, "closed") => "Closed", _ => state
    };

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionTabs.SelectedItem is TerminalSessionSnapshot selected) _selectedSessionId = selected.Id;
        RefreshSelected();
        if (CommandBox.IsEnabled) Dispatcher.BeginInvoke(() => CommandBox.Focus());
    }
    private void NewPowerShell_Click(object sender, RoutedEventArgs e) => ToggleNewTerminalPopup(NewPowerShellButton);
    private void NewTerminalMenu_Click(object sender, RoutedEventArgs e) => ToggleNewTerminalPopup(NewTerminalMenuButton);
    private void ToggleNewTerminalPopup(UIElement target)
    {
        NewTerminalPopup.PlacementTarget = target;
        NewTerminalPopup.IsOpen = !NewTerminalPopup.IsOpen;
    }
    private void PopupPowerShell_Click(object sender, RoutedEventArgs e) { NewTerminalPopup.IsOpen = false; StartSession("powershell"); }
    private void PopupCmd_Click(object sender, RoutedEventArgs e) { NewTerminalPopup.IsOpen = false; StartSession("cmd"); }

    private async void CommandBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunCommandAsync();
    }

    private void TerminalPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || CancelButton.Visibility != Visibility.Visible) return;
        e.Handled = true;
        Cancel_Click(sender, e);
    }

    private async Task RunCommandAsync()
    {
        var session = SelectedSession();
        if (_service is null || _authorize is null || session is null || string.IsNullOrWhiteSpace(CommandBox.Text)) return;
        var command = CommandBox.Text;
        try
        {
            if (command.Trim().Equals("Clear-Host", StringComparison.OrdinalIgnoreCase) || command.Trim().Equals("cls", StringComparison.OrdinalIgnoreCase))
            {
                _service.ClearOutput(session.Id);
                CommandBox.Clear();
                RefreshSessions();
                CommandBox.Focus();
                return;
            }
            if (!await _authorize(session.Shell, command, session.WorkingDirectory, CancellationToken.None))
            {
                TerminalState.Text = _language == "de" ? "Ausführung nicht freigegeben." : "Execution was not approved.";
                return;
            }
            CommandBox.Clear();
            await _service.ExecuteAsync(session.Id, command, CancellationToken.None);
            RefreshSessions();
            CommandBox.Focus();
        }
        catch (Exception ex) { TerminalState.Text = ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (_service is null || session is null) return;
        try { _service.Cancel(session.Id); RefreshSessions(); }
        catch (Exception ex) { TerminalState.Text = ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (_service is null || session is null) return;
        if (session.State == "running" && System.Windows.MessageBox.Show(_language == "de" ? "Laufenden Befehl beenden und diesen Tab schließen?" : "Stop the running command and close this tab?", "Jarvis", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _service.Close(session.Id);
        RefreshSessions();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (_service is null || session is null) return;
        _service.ClearOutput(session.Id);
        RefreshSessions();
    }
}
