using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Jarvis.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace Jarvis;

public partial class WorkspaceView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<WorkspaceDocument> _documents = [];
    private readonly ObservableCollection<BrowserWorkspaceTab> _browserTabs = [];
    private AppConfig? _config;
    private FileChangeTrackingService? _changes;
    private Func<Task>? _persist;
    private WorkspaceDocument? _activeDocument;
    private bool _loadingEditor;
    private string _language = "en";
    private string? _root;
    private readonly HashSet<string> _expandedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? CollapseRequested;
    public event EventHandler? SplitRequested;
    public event Action<object?, string>? MaximizeRequested;
    public event Action<object?, string?>? ProjectRootChanged;
    public event Action<object?, string>? ViewChanged;
    public string? ProjectRoot => _root;

    public WorkspaceView()
    {
        InitializeComponent();
        DocumentTabs.ItemsSource = _documents;
        BrowserTabs.ItemsSource = _browserTabs;
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretStatus.Text = $"Ln {CodeEditor.TextArea.Caret.Line}, Col {CodeEditor.TextArea.Caret.Column}";
    }

    public async Task ConfigureAsync(AppConfig config, FileChangeTrackingService changes, Func<Task> persist, string defaultRoot)
    {
        _config = config; _changes = changes; _persist = persist; _language = config.UiLanguage == "de" ? "de" : "en";
        ApplyLanguage();
        _root = Directory.Exists(config.WorkspaceRoot) ? Path.GetFullPath(config.WorkspaceRoot) : null;
        _expandedDirectories.Clear();
        if (_root is not null)
            foreach (var directory in (config.WorkspaceExpandedDirectories ?? []).Where(Directory.Exists))
                if (IsWithinRoot(directory, _root)) _expandedDirectories.Add(Path.GetFullPath(directory));
        RefreshTree();
        foreach (var file in (config.WorkspaceOpenFiles ?? []).Where(File.Exists).Take(20))
            await OpenFileAsync(file, false);
        if (File.Exists(config.WorkspaceActiveFile)) await OpenFileAsync(config.WorkspaceActiveFile, false);
        foreach (var url in (config.WorkspaceBrowserUrls ?? []).Take(8))
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                await CreateBrowserTabAsync(uri.AbsoluteUri, false);
        if (_browserTabs.Count == 0) await CreateBrowserTabAsync("about:blank", false);
        ShowView(config.WorkspaceActiveView);
        RefreshChanges();
    }

    public void ConfigureTerminal(TerminalSessionService service, Func<IReadOnlyCollection<string>> allowedRoots,
        Func<string> defaultWorkingDirectory, Func<string, string, string, CancellationToken, Task<bool>> authorize, string language)
        => WorkspaceTerminalPanel.Configure(service, allowedRoots, defaultWorkingDirectory, authorize, language);
    public Task<bool> SelectProjectRootAsync(string path) => SetProjectRootAsync(path);

    public void ApplyLanguage(string? language = null)
    {
        if (language is not null) _language = language == "de" ? "de" : "en";
        WorkspaceTitle.Text = T("Arbeitsbereich", "Workspace");
        EditorTab.Header = T("Dateien", "Files"); BrowserTab.Header = T("Browser", "Browser"); ChangesTab.Header = T("Änderungen", "Changes"); TerminalTab.Header = T("Terminal", "Terminal");
        CollapseButton.ToolTip = T("Arbeitsbereich schließen", "Close workspace");
        ProjectRootBox.ToolTip = T("Ausgewählter Projektordner", "Selected project folder");
        BrowseRootButton.Content = T("Ordner auswählen…", "Choose folder…");
        ClearRootButton.Content = T("Aufheben", "Clear");
        ClearRootButton.ToolTip = T("Projektordner-Auswahl aufheben", "Clear project folder selection");
        HeaderRefreshButton.ToolTip = T("Projekt aktualisieren", "Refresh project");
        WorkspaceTerminalPanel.ApplyLanguage(_language);
        SaveButton.Content = T("Speichern", "Save"); ReloadButton.Content = T("Neu laden", "Reload");
        FindNextButton.Content = T("Weitersuchen", "Find next");
        FindNextButton.ToolTip = EditorFindBox.ToolTip = T("Aktuelle Datei durchsuchen", "Search current file");
        NavigatePathButton.Content = T("Öffnen", "Go");
        NavigatePathBox.ToolTip = T("Datei oder Ordner über Pfad öffnen", "Open file or folder by path");
        ExplorerHeading.Text = T("Projektdateien", "Project files");
        RefreshChanges();
    }

    public void ShowView(string view)
    {
        WorkspaceTabs.SelectedItem = view.Equals("Browser", StringComparison.OrdinalIgnoreCase) ? BrowserTab :
            view.Equals("Changes", StringComparison.OrdinalIgnoreCase) ? ChangesTab :
            view.Equals("Terminal", StringComparison.OrdinalIgnoreCase) ? TerminalTab : EditorTab;
        if (view.Equals("Terminal", StringComparison.OrdinalIgnoreCase)) WorkspaceTerminalPanel.EnsureSession();
        ViewChanged?.Invoke(this, view);
    }

    public async Task OpenFileAsync(string path, bool select = true)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("File does not exist.", path);
        var info = new FileInfo(path);
        if (info.Length > 8 * 1024 * 1024) throw new IOException("The integrated editor is limited to files up to 8 MB.");
        var existing = _documents.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var (text, encoding) = await ReadTextAsync(path);
            existing = new WorkspaceDocument(path, text, encoding, info.LastWriteTimeUtc);
            _documents.Add(existing);
        }
        if (select)
        {
            ShowView("Files");
            DocumentTabs.SelectedItem = existing;
        }
        await SaveStateAsync();
    }

    public async Task<(bool Ok, string Url, string Title, string Error)> NavigateBrowserAsync(string rawUrl, CancellationToken token)
    {
        if (!TryNormalizeUri(rawUrl, out var uri)) return (false, "", "", T("Nur absolute HTTP-/HTTPS-Adressen sind erlaubt.", "Only absolute HTTP and HTTPS URLs are allowed."));
        ShowView("Browser");
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab ?? _browserTabs.FirstOrDefault() ?? await CreateBrowserTabAsync("about:blank", true);
        try { await EnsureBrowserAsync(tab); }
        catch (Exception ex) { BrowserStatus.Text = T("Browser nicht verfügbar: ", "Browser unavailable: ") + ex.Message; return (false, "", "", BrowserStatus.Text); }
        if (tab.View.CoreWebView2 is null) return (false, "", "", BrowserStatus.Text);
        var done = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) => done.TrySetResult(args);
        tab.View.NavigationCompleted += Handler;
        try
        {
            tab.View.CoreWebView2.Navigate(uri.AbsoluteUri);
            using var registration = token.Register(() => done.TrySetCanceled(token));
            var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(45), token);
            tab.Url = tab.View.Source?.AbsoluteUri ?? uri.AbsoluteUri;
            tab.Title = string.IsNullOrWhiteSpace(tab.View.CoreWebView2.DocumentTitle) ? uri.Host : tab.View.CoreWebView2.DocumentTitle;
            await SaveStateAsync();
            return result.IsSuccess ? (true, tab.Url, tab.Title, "") : (false, tab.Url, tab.Title, result.WebErrorStatus.ToString());
        }
        catch (TimeoutException) { return (false, uri.AbsoluteUri, "", T("Browser-Zeitüberschreitung.", "Browser navigation timed out.")); }
        finally { tab.View.NavigationCompleted -= Handler; }
    }

    public async Task<(bool Ok, string Url, string Title, string Error)> OpenBrowserTabAsync(string rawUrl, CancellationToken token)
    {
        if (!TryNormalizeUri(rawUrl, out var uri)) return (false, "", "", T("Nur absolute HTTP-/HTTPS-Adressen sind erlaubt.", "Only absolute HTTP and HTTPS URLs are allowed."));
        ShowView("Browser");
        await CreateBrowserTabAsync("about:blank", true);
        return await NavigateBrowserAsync(uri.AbsoluteUri, token);
    }

    public bool SelectBrowserTab(int index)
    {
        if (index < 0 || index >= _browserTabs.Count) return false;
        ShowView("Browser"); BrowserTabs.SelectedIndex = index; return true;
    }

    public IReadOnlyList<(int Index, string Title, string Url, bool Active)> BrowserTabStates() =>
        _browserTabs.Select((tab, index) => (index, tab.Title, tab.Url, ReferenceEquals(BrowserTabs.SelectedItem, tab))).ToArray();

    public async Task<(bool Ok, string State, string Error)> WaitForBrowserAsync(int timeoutMilliseconds, CancellationToken token)
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab;
        if (tab is null) return (false, "missing", T("Kein Browser-Tab ist aktiv.", "No browser tab is active."));
        try { await EnsureBrowserAsync(tab); }
        catch (Exception ex) { return (false, "unavailable", ex.Message); }
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, 100, 30_000));
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            token.ThrowIfCancellationRequested();
            var json = await tab.View.ExecuteScriptAsync("document.readyState");
            var state = JsonSerializer.Deserialize<string>(json) ?? "unknown";
            if (state.Equals("complete", StringComparison.OrdinalIgnoreCase)) return (true, state, "");
            await Task.Delay(100, token);
        }
        return (false, "timeout", T("Browser-Zeitüberschreitung.", "Browser navigation timed out."));
    }

    public async Task<string> ReadCurrentPageAsync(CancellationToken token)
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab ?? throw new InvalidOperationException("No browser tab is active.");
        await EnsureBrowserAsync(tab); token.ThrowIfCancellationRequested();
        var json = await tab.View.ExecuteScriptAsync("JSON.stringify({title:document.title,url:location.href,text:(document.body?.innerText||'').slice(0,30000)})");
        return JsonSerializer.Deserialize<string>(json) ?? "{}";
    }

    public (bool Initialized, string Url, string Title, bool CanGoBack, bool CanGoForward) CurrentBrowserState()
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab;
        return tab is null ? (false, "", "", false, false) :
            (tab.View.CoreWebView2 is not null, tab.Url, tab.Title, tab.View.CanGoBack, tab.View.CanGoForward);
    }

    public async Task<string> ClickAsync(string selector, CancellationToken token)
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab ?? throw new InvalidOperationException("No browser tab is active.");
        await EnsureBrowserAsync(tab); token.ThrowIfCancellationRequested();
        var selectorJson = JsonSerializer.Serialize(selector);
        var script = $"(()=>{{const e=document.querySelector({selectorJson});if(!e)return JSON.stringify({{ok:false,error:'Element not found'}});e.click();return JSON.stringify({{ok:true,tag:e.tagName,text:(e.innerText||e.getAttribute('aria-label')||'').slice(0,300)}})}})()";
        return JsonSerializer.Deserialize<string>(await tab.View.ExecuteScriptAsync(script)) ?? "{}";
    }

    public async Task<string> FillAsync(string selector, string value, CancellationToken token)
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab ?? throw new InvalidOperationException("No browser tab is active.");
        await EnsureBrowserAsync(tab); token.ThrowIfCancellationRequested();
        var script = $"(()=>{{const e=document.querySelector({JsonSerializer.Serialize(selector)});if(!e)return JSON.stringify({{ok:false,error:'Element not found'}});e.focus();e.value={JsonSerializer.Serialize(value)};e.dispatchEvent(new Event('input',{{bubbles:true}}));e.dispatchEvent(new Event('change',{{bubbles:true}}));return JSON.stringify({{ok:true}})}})()";
        return JsonSerializer.Deserialize<string>(await tab.View.ExecuteScriptAsync(script)) ?? "{}";
    }

    public async Task ScrollAsync(double y, CancellationToken token)
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab ?? throw new InvalidOperationException("No browser tab is active.");
        await EnsureBrowserAsync(tab); token.ThrowIfCancellationRequested();
        await tab.View.ExecuteScriptAsync($"window.scrollBy({{top:{y.ToString(System.Globalization.CultureInfo.InvariantCulture)},behavior:'smooth'}})");
    }

    public void RefreshChanges()
    {
        if (_changes is null || ChangesPanel is null) return;
        ChangesPanel.Children.Clear();
        var records = _changes.Snapshot().OrderByDescending(item => item.Timestamp).ToArray();
        ChangeSummary.Text = records.Length == 0 ? T("Keine nachverfolgten Änderungen", "No tracked changes") :
            $"{records.Length} {T("Dateien geändert", "files changed")}   +{records.Sum(x => x.AddedLines)}  -{records.Sum(x => x.RemovedLines)}";
        foreach (var change in records.Take(250)) ChangesPanel.Children.Add(CreateChangeCard(change));
    }

    private FrameworkElement CreateChangeCard(FileChangeRecord change)
    {
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = change.Path, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock { Text = change.Reason, Foreground = new SolidColorBrush(Color.FromRgb(142, 141, 153)), Margin = new Thickness(0, 3, 0, 6), TextWrapping = TextWrapping.Wrap });
        if (change.IsBinary)
            details.Children.Add(new TextBlock { Text = T("Binärdatei – Dateimetadaten geändert", "Binary file – file metadata changed"), Foreground = Brushes.LightGray });
        else
        {
            var diff = new StackPanel();
            foreach (var line in FileChangeTrackingService.BuildDiff(change.BeforeText, change.AfterText).Take(1000))
            {
                var prefix = line.Kind == DiffLineKind.Added ? "+ " : line.Kind == DiffLineKind.Removed ? "- " : "  ";
                diff.Children.Add(new TextBlock {
                    Text = prefix + line.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
                    Foreground = line.Kind == DiffLineKind.Added ? new SolidColorBrush(Color.FromRgb(93, 209, 139)) :
                        line.Kind == DiffLineKind.Removed ? new SolidColorBrush(Color.FromRgb(239, 105, 112)) : Brushes.LightGray,
                    Background = line.Kind == DiffLineKind.Added ? new SolidColorBrush(Color.FromArgb(35, 52, 135, 82)) :
                        line.Kind == DiffLineKind.Removed ? new SolidColorBrush(Color.FromArgb(35, 150, 45, 52)) : Brushes.Transparent
                });
            }
            details.Children.Add(new ScrollViewer { Content = diff, MaxHeight = 430, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (File.Exists(change.Path))
        {
            var open = new Button { Content = T("Datei öffnen", "Open file"), Tag = change.Path };
            open.Click += async (_, _) => await OpenFileAsync((string)open.Tag);
            actions.Children.Add(open);
        }
        if (change.RollbackAvailable)
        {
            var rollback = new Button { Content = T("Rückgängig", "Rollback"), Tag = change.Id, Margin = new Thickness(5, 0, 0, 0) };
            rollback.Click += Rollback_Click;
            actions.Children.Add(rollback);
        }
        details.Children.Add(actions);
        var header = new DockPanel();
        header.Children.Add(new TextBlock { Text = $"{ChangeKindLabel(change.Kind)} · {Path.GetFileName(change.Path)}", FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = $"  +{change.AddedLines}  -{change.RemovedLines}", Foreground = new SolidColorBrush(Color.FromRgb(93, 209, 139)), HorizontalAlignment = System.Windows.HorizontalAlignment.Right });
        return new Expander { Header = header, Content = details, IsExpanded = false, Background = new SolidColorBrush(Color.FromRgb(29, 30, 37)), BorderBrush = new SolidColorBrush(Color.FromRgb(52, 54, 64)), BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6) };
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_changes is null || sender is not Button { Tag: string id }) return;
        var result = await _changes.RollbackAsync(id);
        MessageBox.Show(result.Message, T("Dateiänderung", "File change"), MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RefreshChanges(); RefreshTree();
    }

    private void RefreshTree()
    {
        if (ProjectTree is null) return;
        ProjectTree.Items.Clear(); WorkspacePath.Text = _root ?? T("Kein Projektordner", "No project folder");
        ProjectRootBox.Text = _root ?? "";
        if (_root is null || !Directory.Exists(_root)) return;
        var root = CreateNode(_root, true); ProjectTree.Items.Add(root); root.IsExpanded = true;
    }
    private TreeViewItem CreateNode(string path, bool directory)
    {
        var name = Path.GetFileName(path).Length == 0 ? path : Path.GetFileName(path);
        var header = new TextBlock
        {
            Text = (directory ? "📁 " : FileIcon(path) + " ") + name,
            ToolTip = path,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 215,
            Foreground = new SolidColorBrush(Color.FromRgb(226, 225, 234))
        };
        var item = new TreeViewItem { Header = header, Tag = path, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        if (directory)
        {
            item.Items.Add(new TreeViewItem { Tag = "__load", Header = T("Wird geladen…", "Loading…") });
            item.Expanded += Node_Expanded;
            item.Collapsed += Node_Collapsed;
        }
        return item;
    }
    private void Node_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || node.Tag is not string path || node.Items.Count != 1 || node.Items[0] is not TreeViewItem { Tag: "__load" }) return;
        node.Items.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).Where(p => !IsIgnored(p)).Order(StringComparer.OrdinalIgnoreCase).Take(500))
            {
                var child = CreateNode(dir, true); node.Items.Add(child);
                if (_expandedDirectories.Contains(Path.GetFullPath(dir))) child.IsExpanded = true;
            }
            foreach (var file in Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase).Take(1500)) node.Items.Add(CreateNode(file, false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { node.Items.Add(new TreeViewItem { Header = "⚠ " + ex.Message, IsEnabled = false }); }
        _expandedDirectories.Add(Path.GetFullPath(path));
        _ = SaveStateAsync();
    }
    private void Node_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { Tag: string path })
        {
            _expandedDirectories.Remove(Path.GetFullPath(path));
            _ = SaveStateAsync();
        }
    }
    private async void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (e.NewValue is TreeViewItem { Tag: string path } && File.Exists(path)) await OpenFileAsync(path); }

    private async void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = T("Projektordner auswählen", "Choose project folder"), UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await SetProjectRootAsync(dialog.SelectedPath);
    }
    private async void ClearRoot_Click(object sender, RoutedEventArgs e) => await ClearProjectRootAsync();
    public async Task ClearProjectRootAsync()
    {
        _root = null;
        _expandedDirectories.Clear();
        ProjectRootBox.Text = NavigatePathBox.Text = WorkspacePath.Text = "";
        ProjectTree.Items.Clear();
        EditorStatus.Text = T("Kein Projektordner ausgewählt.", "No project folder selected.");
        ProjectRootChanged?.Invoke(this, null);
        await SaveStateAsync();
    }
    private async void ApplyRoot_Click(object sender, RoutedEventArgs e) => await SetProjectRootAsync(ProjectRootBox.Text);
    private async void ProjectRoot_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; await SetProjectRootAsync(ProjectRootBox.Text);
    }
    private async Task<bool> SetProjectRootAsync(string? rawPath)
    {
        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables((rawPath ?? "").Trim().Trim('"'))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { EditorStatus.Text = ex.Message; return false; }
        if (!Directory.Exists(path)) { EditorStatus.Text = T("Projektpfad ist ungültig oder nicht erreichbar.", "Project path is invalid or inaccessible."); return false; }
        try { _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { EditorStatus.Text = ex.Message; return false; }
        _root = path; _expandedDirectories.Clear(); NavigatePathBox.Text = path; RefreshTree();
        ProjectRootChanged?.Invoke(this, path); await SaveStateAsync(); return true;
    }
    private void RefreshTree_Click(object sender, RoutedEventArgs e) => RefreshTree();
    private void FileSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_root is null || !Directory.Exists(_root) || string.IsNullOrWhiteSpace(FileSearchBox.Text)) { RefreshTree(); return; }
        ProjectTree.Items.Clear();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains(FileSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(300))
                ProjectTree.Items.Add(CreateNode(file, false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { EditorStatus.Text = ex.Message; }
    }

    private async void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeDocument is not null && !_loadingEditor) _activeDocument.Text = CodeEditor.Text;
        _activeDocument = DocumentTabs.SelectedItem as WorkspaceDocument;
        _loadingEditor = true;
        try
        {
            CodeEditor.Text = _activeDocument?.Text ?? ""; CurrentFilePath.Text = _activeDocument?.Path ?? "";
            CodeEditor.SyntaxHighlighting = ResolveHighlighting(_activeDocument?.Path);
            SaveButton.IsEnabled = ReloadButton.IsEnabled = FindNextButton.IsEnabled = _activeDocument is not null;
            EditorStatus.Text = _activeDocument is null ? T("Datei im Projektbaum auswählen", "Select a file in the project tree") : SyntaxLabel(_activeDocument.Path);
        }
        finally { _loadingEditor = false; }
        await SaveStateAsync();
    }
    private void CodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_loadingEditor || _activeDocument is null) return;
        _activeDocument.Text = CodeEditor.Text; _activeDocument.IsDirty = !string.Equals(_activeDocument.Text, _activeDocument.OriginalText, StringComparison.Ordinal);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null) return;
        var currentStamp = File.Exists(_activeDocument.Path) ? File.GetLastWriteTimeUtc(_activeDocument.Path) : DateTime.MinValue;
        if (currentStamp != _activeDocument.LastWriteUtc)
        {
            var choice = MessageBox.Show(T("Die Datei wurde außerhalb von Jarvis geändert. Trotzdem überschreiben?", "The file changed outside Jarvis. Overwrite it anyway?"), T("Dateikonflikt", "File conflict"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;
        }
        await File.WriteAllTextAsync(_activeDocument.Path, CodeEditor.Text, _activeDocument.Encoding);
        _activeDocument.AcceptSaved(CodeEditor.Text, File.GetLastWriteTimeUtc(_activeDocument.Path));
        EditorStatus.Text = T("Gespeichert", "Saved") + " · " + DateTime.Now.ToString("T");
    }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null) return;
        if (_activeDocument.IsDirty && MessageBox.Show(T("Ungespeicherte Änderungen verwerfen?", "Discard unsaved changes?"), T("Neu laden", "Reload"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var (text, encoding) = await ReadTextAsync(_activeDocument.Path);
        _activeDocument.Encoding = encoding; _activeDocument.AcceptSaved(text, File.GetLastWriteTimeUtc(_activeDocument.Path));
        _loadingEditor = true; CodeEditor.Text = text; _loadingEditor = false;
    }
    private async void CloseDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkspaceDocument document }) return;
        e.Handled = true;
        if (document.IsDirty && MessageBox.Show(T("Ungespeicherte Änderungen verwerfen?", "Discard unsaved changes?"), document.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _documents.Remove(document); if (ReferenceEquals(_activeDocument, document)) DocumentTabs.SelectedItem = _documents.LastOrDefault();
        await SaveStateAsync();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNextInEditor();
    private void EditorFind_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FindNextInEditor();
    }
    private void FindNextInEditor()
    {
        if (_activeDocument is null || string.IsNullOrEmpty(EditorFindBox.Text)) return;
        var query = EditorFindBox.Text;
        var start = Math.Clamp(CodeEditor.SelectionStart + CodeEditor.SelectionLength, 0, CodeEditor.Text.Length);
        var index = CodeEditor.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && start > 0) index = CodeEditor.Text.IndexOf(query, 0, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { EditorStatus.Text = T("Kein weiterer Treffer", "No further match"); return; }
        CodeEditor.Select(index, query.Length);
        CodeEditor.TextArea.Caret.Offset = index + query.Length;
        CodeEditor.ScrollToLine(CodeEditor.Document.GetLineByOffset(index).LineNumber);
        EditorStatus.Text = T("Treffer gefunden", "Match found");
    }

    private async void NavigatePath_Click(object sender, RoutedEventArgs e) => await NavigateToPathAsync();
    private async void NavigatePath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateToPathAsync();
    }
    private async Task NavigateToPathAsync()
    {
        if (string.IsNullOrWhiteSpace(NavigatePathBox.Text)) return;
        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(NavigatePathBox.Text.Trim().Trim('"'))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { EditorStatus.Text = ex.Message; return; }
        if (File.Exists(path)) { await OpenFileAsync(path); return; }
        if (Directory.Exists(path))
        {
            await SetProjectRootAsync(path); return;
        }
        EditorStatus.Text = T("Pfad wurde nicht gefunden", "Path was not found");
    }

    private async Task<BrowserWorkspaceTab> CreateBrowserTabAsync(string url, bool select)
    {
        var view = new WebView2();
        var tab = new BrowserWorkspaceTab(view, url);
        view.NavigationStarting += (_, args) => { if (!TryNormalizeUri(args.Uri, out var _) && !args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) { args.Cancel = true; BrowserStatus.Text = T("Unsichere Navigation blockiert", "Unsafe navigation blocked"); } else BrowserStatus.Text = T("Wird geladen…", "Loading…"); };
        view.SourceChanged += (_, _) => { tab.Url = view.Source?.AbsoluteUri ?? tab.Url; if (ReferenceEquals(BrowserTabs.SelectedItem, tab)) AddressBox.Text = tab.Url; };
        view.NavigationCompleted += (_, args) => { tab.Title = view.CoreWebView2?.DocumentTitle ?? new Uri(tab.Url).Host; BrowserStatus.Text = args.IsSuccess ? T("Geladen", "Loaded") : $"{T("Navigationsfehler", "Navigation error")}: {args.WebErrorStatus}"; _ = SaveStateAsync(); };
        _browserTabs.Add(tab);
        if (select) BrowserTabs.SelectedItem = tab;
        try
        {
            await EnsureBrowserAsync(tab);
            if (url != "about:blank") view.CoreWebView2?.Navigate(url);
        }
        catch (Exception ex) { BrowserStatus.Text = T("Browser nicht verfügbar: ", "Browser unavailable: ") + ex.Message; }
        return tab;
    }
    private async Task EnsureBrowserAsync(BrowserWorkspaceTab tab)
    {
        if (tab.View.CoreWebView2 is not null) return;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, data);
        await tab.View.EnsureCoreWebView2Async(environment);
        var core = tab.View.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not initialize.");
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested += (_, args) => { args.Handled = true; _ = Dispatcher.InvokeAsync(async () => await CreateBrowserTabAsync(args.Uri, true)); };
    }
    private async void NewBrowserTab_Click(object sender, RoutedEventArgs e) => await CreateBrowserTabAsync("about:blank", true);
    private async void CloseBrowserTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowserWorkspaceTab tab }) return; e.Handled = true;
        _browserTabs.Remove(tab); tab.View.Dispose();
        if (_browserTabs.Count == 0) await CreateBrowserTabAsync("about:blank", true); else BrowserTabs.SelectedItem = _browserTabs.Last();
        await SaveStateAsync();
    }
    private void BrowserTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tab = BrowserTabs.SelectedItem as BrowserWorkspaceTab; BrowserHost.Content = tab?.View; AddressBox.Text = tab?.Url ?? "";
    }
    private async void BrowserGo_Click(object sender, RoutedEventArgs e) => await NavigateBrowserAsync(AddressBox.Text, CancellationToken.None);
    private async void AddressBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await NavigateBrowserAsync(AddressBox.Text, CancellationToken.None); } }
    private void BrowserBack_Click(object sender, RoutedEventArgs e) { if (BrowserHost.Content is WebView2 { CanGoBack: true } view) view.GoBack(); }
    private void BrowserForward_Click(object sender, RoutedEventArgs e) { if (BrowserHost.Content is WebView2 { CanGoForward: true } view) view.GoForward(); }
    private void BrowserReload_Click(object sender, RoutedEventArgs e) { if (BrowserHost.Content is WebView2 view) view.Reload(); }
    private void RefreshChanges_Click(object sender, RoutedEventArgs e) => RefreshChanges();
    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceTabs.SelectedItem == TerminalTab) WorkspaceTerminalPanel.EnsureSession();
        await SaveStateAsync();
    }
    private void Collapse_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);
    private void Split_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(this, EventArgs.Empty);
    private void Maximize_Click(object sender, RoutedEventArgs e) => MaximizeRequested?.Invoke(this,
        WorkspaceTabs.SelectedItem == BrowserTab ? "Browser" : WorkspaceTabs.SelectedItem == ChangesTab ? "Changes" : WorkspaceTabs.SelectedItem == TerminalTab ? "Terminal" : "Files");

    private async Task SaveStateAsync()
    {
        if (_config is null) return;
        _config.WorkspaceRoot = _root ?? "";
        _config.WorkspaceOpenFiles = _documents.Select(item => item.Path).ToList();
        _config.WorkspaceActiveFile = _activeDocument?.Path ?? "";
        _config.WorkspaceExpandedDirectories = _expandedDirectories.Where(Directory.Exists).Take(200).ToList();
        _config.WorkspaceBrowserUrls = _browserTabs.Select(item => item.Url).Where(url => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        _config.WorkspaceActiveView = WorkspaceTabs.SelectedItem == BrowserTab ? "Browser" : WorkspaceTabs.SelectedItem == ChangesTab ? "Changes" : WorkspaceTabs.SelectedItem == TerminalTab ? "Terminal" : "Files";
        if (_persist is not null) await _persist();
    }
    private static bool IsWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<(string Text, Encoding Encoding)> ReadTextAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Take(Math.Min(bytes.Length, 4096)).Any(value => value == 0)) throw new IOException("Binary files cannot be edited as text.");
        var encoding = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? new UTF8Encoding(true) : new UTF8Encoding(false);
        return (encoding.GetString(bytes).TrimStart('\uFEFF'), encoding);
    }
    public static IHighlightingDefinition? ResolveHighlighting(string? path)
    {
        if (path is null) return null;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var name = SyntaxLanguageCatalog.DefinitionFor(path);
        return name is null ? HighlightingManager.Instance.GetDefinitionByExtension(extension) :
            HighlightingManager.Instance.GetDefinition(name) ?? HighlightingManager.Instance.GetDefinitionByExtension(extension) ?? CreateFallbackHighlighting(name);
    }
    private static readonly Dictionary<string, IHighlightingDefinition> CustomHighlighting = new(StringComparer.OrdinalIgnoreCase);
    private static IHighlightingDefinition CreateFallbackHighlighting(string name)
    {
        if (CustomHighlighting.TryGetValue(name, out var existing)) return existing;
        var keywords = name switch
        {
            "Python" => "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield",
            "YAML" => "true false null yes no on off",
            "INI" => "true false yes no on off",
            "Bash" => "if then else elif fi for while in do done case esac function select until time",
            _ => "true false null"
        };
        var comment = name is "Python" or "YAML" or "Bash" ? "#" : name == "INI" ? ";" : "//";
        var words = string.Concat(keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => $"<Word>{word}</Word>"));
        var xml = $"""<SyntaxDefinition name="{name}" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"><Color name="Comment" foreground="#6A9955"/><Color name="String" foreground="#CE9178"/><Color name="Number" foreground="#B5CEA8"/><Color name="Keyword" foreground="#C586C0" fontWeight="bold"/><RuleSet><Span color="Comment" begin="{System.Security.SecurityElement.Escape(comment)}" end="\n"/><Span color="String" begin="&quot;" end="&quot;"/><Span color="String" begin="'" end="'"/><Rule color="Number">\b[0-9]+(?:\.[0-9]+)?\b</Rule><Keywords color="Keyword">{words}</Keywords></RuleSet></SyntaxDefinition>""";
        using var reader = XmlReader.Create(new StringReader(xml));
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        CustomHighlighting[name] = definition;
        return definition;
    }
    private static string SyntaxLabel(string path) => $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · UTF-8";
    private static bool TryNormalizeUri(string? value, out Uri uri)
    {
        var text = (value ?? "").Trim(); if (!text.Contains("://", StringComparison.Ordinal) && !text.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out uri!) && ((uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)) || uri.AbsoluteUri == "about:blank");
    }
    private static bool IsIgnored(string path) => Path.GetFileName(path) is ".git" or "bin" or "obj" or "node_modules";
    private static string FileIcon(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".cs" => "◇", ".xaml" => "▱", ".json" => "{}", ".md" => "M", ".png" or ".jpg" or ".jpeg" => "▣", _ => "▤" };
    private string T(string de, string en) => _language == "de" ? de : en;
    private string ChangeKindLabel(FileChangeKind kind) => _language == "de" ? kind switch
    {
        FileChangeKind.Added => "Neu", FileChangeKind.Modified => "Geändert", FileChangeKind.Removed => "Gelöscht",
        FileChangeKind.Moved => "Verschoben", FileChangeKind.Renamed => "Umbenannt", FileChangeKind.FolderCreated => "Ordner erstellt",
        FileChangeKind.FolderRemoved => "Ordner gelöscht", _ => kind.ToString()
    } : kind.ToString();
}

public sealed class WorkspaceDocument : INotifyPropertyChanged
{
    private bool _dirty;
    public WorkspaceDocument(string path, string text, Encoding encoding, DateTime lastWriteUtc) { Path = path; Text = OriginalText = text; Encoding = encoding; LastWriteUtc = lastWriteUtc; }
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Text { get; set; }
    public string OriginalText { get; private set; }
    public Encoding Encoding { get; set; }
    public DateTime LastWriteUtc { get; private set; }
    public bool IsDirty { get => _dirty; set { if (_dirty == value) return; _dirty = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    public string DisplayName => Name + (IsDirty ? " ●" : "");
    public void AcceptSaved(string text, DateTime timestamp) { Text = OriginalText = text; LastWriteUtc = timestamp; IsDirty = false; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BrowserWorkspaceTab : INotifyPropertyChanged
{
    private string _title;
    public BrowserWorkspaceTab(WebView2 view, string url) { View = view; Url = url; _title = url == "about:blank" ? "New tab" : new Uri(url).Host; }
    public WebView2 View { get; }
    public string Url { get; set; }
    public string Title { get => _title; set { if (_title == value) return; _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
