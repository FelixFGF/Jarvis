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

    public TerminalPanel() { InitializeComponent(); SessionBox.ItemsSource = _sessions; }

    public void Configure(TerminalSessionService service, Func<IReadOnlyCollection<string>> allowedRoots,
        Func<string> defaultWorkingDirectory, Func<string, string, string, CancellationToken, Task<bool>> authorize, string language)
    {
        if (_service is not null) _service.SessionChanged -= Service_SessionChanged;
        _service = service; _allowedRoots = allowedRoots; _defaultWorkingDirectory = defaultWorkingDirectory; _authorize = authorize;
        _service.SessionChanged += Service_SessionChanged; ApplyLanguage(language); RefreshSessions();
        DefaultDirectoryLabel.Text = defaultWorkingDirectory();
    }

    public void ApplyLanguage(string language)
    {
        _language = language == "de" ? "de" : "en"; var de = _language == "de";
        ClearButton.Content = de ? "Leeren" : "Clear"; ClearButton.ToolTip = de ? "Nur die sichtbare Ausgabe leeren" : "Clear visible output only";
        CloseButton.Content = de ? "Schließen" : "Close";
        NewSessionButton.Content = de ? "Neu" : "New"; NewSessionButton.ToolTip = de ? "Neue Terminalsitzung starten" : "Start a new terminal session";
        CancelButton.Content = de ? "Stopp" : "Stop";
        CancelButton.ToolTip = de ? "Laufenden Befehl abbrechen (Strg+C)" : "Interrupt running command (Ctrl+C)";
        CommandBox.ToolTip = de ? "Befehl eingeben und Enter drücken" : "Enter a command and press Enter";
        RefreshSelected();
    }

    public bool EnsureSession()
    {
        if (_sessions.Count > 0) return true;
        if (_service is null || _allowedRoots is null) return false;
        try
        {
            var shell = ShellBox.SelectedValue?.ToString() ?? "powershell";
            var cwd = _defaultWorkingDirectory?.Invoke() ?? "";
            DefaultDirectoryLabel.Text = cwd;
            var session = _service.Start(shell, cwd, _allowedRoots());
            RefreshSessions(); SessionBox.SelectedValue = session.Id;
            return true;
        }
        catch (Exception ex) { TerminalState.Text = ex.Message; return false; }
    }

    private void Service_SessionChanged(object? sender, string id) => Dispatcher.BeginInvoke(RefreshSessions);
    private void RefreshSessions()
    {
        var selected = SessionBox.SelectedValue as string;
        _sessions.Clear(); if (_service is not null) foreach (var session in _service.Snapshot()) _sessions.Add(session);
        SessionBox.SelectedValue = selected;
        if (SessionBox.SelectedItem is null && _sessions.Count > 0) SessionBox.SelectedIndex = 0;
        RefreshSelected();
    }
    private void RefreshSelected()
    {
        if (SessionBox.SelectedItem is not TerminalSessionSnapshot selected)
        {
            OutputBox.Text = ErrorBox.Text = ""; CommandBox.IsEnabled = CancelButton.IsEnabled = CloseButton.IsEnabled = ClearButton.IsEnabled = false;
            CancelButton.Visibility = ErrorContainer.Visibility = Visibility.Collapsed;
            PromptLabel.Text = "PS>";
            TerminalState.Text = _language == "de" ? "Keine aktive Terminalsitzung." : "No active terminal session."; WorkingDirectoryLabel.Text = ""; return;
        }
        TerminalSessionSnapshot session;
        try { session = _service?.Get(selected.Id) ?? selected; } catch { session = selected; }
        OutputBox.Text = session.StandardOutput; ErrorBox.Text = session.StandardError;
        OutputBox.ScrollToEnd(); ErrorBox.ScrollToEnd();
        CommandBox.IsEnabled = session.State == "ready"; CancelButton.IsEnabled = session.State == "running"; CloseButton.IsEnabled = ClearButton.IsEnabled = true;
        CancelButton.Visibility = session.State == "running" ? Visibility.Visible : Visibility.Collapsed;
        ErrorContainer.Visibility = string.IsNullOrWhiteSpace(session.StandardError) ? Visibility.Collapsed : Visibility.Visible;
        WorkingDirectoryLabel.Text = session.WorkingDirectory;
        PromptLabel.Text = session.Shell == "cmd" ? $"{session.WorkingDirectory}>" : $"PS {session.WorkingDirectory}>";
        TerminalState.Text = $"{session.Shell} · {LocalizedState(session.State)}" + (session.LastExitCode is int code ? $" · Exit {code}" : "");
    }
    private string LocalizedState(string state) => (_language, state) switch
    {
        ("de", "ready") => "Bereit", ("de", "running") => "Befehl läuft", ("de", "failed") => "Fehlgeschlagen",
        ("de", "cancelled") => "Abgebrochen", ("de", "closed") => "Geschlossen", _ => state
    };

    private void SessionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSelected();
    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _allowedRoots is null) return;
        try
        {
            var shell = ShellBox.SelectedValue?.ToString() ?? "powershell";
            var cwd = _defaultWorkingDirectory?.Invoke() ?? "";
            DefaultDirectoryLabel.Text = cwd;
            var session = _service.Start(shell, cwd, _allowedRoots()); RefreshSessions(); SessionBox.SelectedValue = session.Id;
        }
        catch (Exception ex) { TerminalState.Text = ex.Message; }
    }
    private async void CommandBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; await RunCommandAsync();
    }
    private void TerminalPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || CancelButton.Visibility != Visibility.Visible) return;
        e.Handled = true; Cancel_Click(sender, e);
    }
    private async Task RunCommandAsync()
    {
        if (_service is null || _authorize is null || SessionBox.SelectedItem is not TerminalSessionSnapshot session || string.IsNullOrWhiteSpace(CommandBox.Text)) return;
        var command = CommandBox.Text;
        try
        {
            if (command.Trim().Equals("Clear-Host", StringComparison.OrdinalIgnoreCase) || command.Trim().Equals("cls", StringComparison.OrdinalIgnoreCase))
            {
                _service.ClearOutput(session.Id); CommandBox.Clear(); RefreshSessions(); CommandBox.Focus(); return;
            }
            if (!await _authorize(session.Shell, command, session.WorkingDirectory, CancellationToken.None)) { TerminalState.Text = _language == "de" ? "Ausführung nicht freigegeben." : "Execution was not approved."; return; }
            await _service.ExecuteAsync(session.Id, command, CancellationToken.None); CommandBox.Clear(); RefreshSessions(); CommandBox.Focus();
        }
        catch (Exception ex) { TerminalState.Text = ex.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SessionBox.SelectedItem is not TerminalSessionSnapshot session) return;
        try { _service.Cancel(session.Id); } catch (Exception ex) { TerminalState.Text = ex.Message; }
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SessionBox.SelectedItem is not TerminalSessionSnapshot session) return;
        if (session.State == "running" && System.Windows.MessageBox.Show(_language == "de" ? "Laufenden Befehl beenden und Sitzung schließen?" : "Stop the running command and close the session?", "Jarvis", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _service.Close(session.Id); RefreshSessions();
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || SessionBox.SelectedItem is not TerminalSessionSnapshot session) return;
        _service.ClearOutput(session.Id); RefreshSessions();
    }
}
