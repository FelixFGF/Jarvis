using Microsoft.Win32;
using Jarvis.Models;
using Jarvis.Services;
using Jarvis.Update.Core;
#if JARVIS_BETA
using Jarvis.Beta;
#endif
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace Jarvis;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private const uint WmClose = 0x0010, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const int SwRestore = 9, SwMaximize = 3;
    private const uint KeyUpFlag = 0x0002;
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ChatStore _chatStore = new();
    private readonly ObservableCollection<ChatSession> _chats = [];
    private readonly ObservableCollection<ChatSession> _openTabs = [];
    private List<ChatModelOption> _availableModels = [];
    private string _modelsSource = "";
    private bool _modelDiscoveryFailed;
    private string _modelDiscoveryError = "";
    private readonly List<ChatAttachment> _pendingAttachments = [];
    private readonly Dictionary<string, List<ChatAttachment>> _draftAttachments = new(StringComparer.Ordinal);
    private ChatSession? _activeChat;
    private readonly ObservableCollection<TaskRecord> _tasks = [];
    private readonly ObservableCollection<MemoryRecord> _memories = [];
    private readonly ObservableCollection<ReminderRecord> _reminders = [];
    private readonly DispatcherTimer _reminderTimer;
    private readonly DispatcherTimer _speechDeviceTimer;
    private readonly DispatcherTimer _voiceVadTimer;
    private readonly LocalSpeechService _speech = new();
    private readonly LocalTtsService _tts = new();
    private readonly GitHubUpdateClient _updates = new();
    private bool _automaticUpdatePromptShown;
    private bool _updateCheckRunning;
    private string _lastPromptedUpdateVersion = "";
    private WaveOutEvent? _wakeSoundOutput;
    private AudioFileReader? _wakeSoundReader;
    private Button? _activeTtsButton;
    private Button? _activeTtsStopButton;
    private readonly DispatcherTimer _ttsTimer;
    private CancellationTokenSource? _speechOperation;
    private CancellationTokenSource? _voicePreparation;
    private CancellationTokenSource? _ttsDownloadOperation;
    private TaskCompletionSource? _speechCompletion;
    private bool _speechBusy;
    private bool _speechTranscribing;
    private bool _voiceModeEnabled;
    private bool _voiceModeAwaitingTts;
    private bool _voiceMuted;
    private CancellationTokenSource? _wakeWordCancellation;
    private Task? _wakeWordTask;
    private Task _wakeWordTtsWarmup = Task.CompletedTask;
    private Task _wakeWordSpeechWarmup = Task.CompletedTask;
    private bool _wakeWordListening;
    private byte[]? _wakeWordPrefillAudio;
    private WaveFormat? _wakeWordPrefillFormat;
    private bool _wakeWordPrefillPending;
    private bool _voiceSpeechDetected;
    private bool _voiceStopScheduled;
    private bool _voiceBargeInMonitoring;
    private DateTime _voiceBargeInCandidateSince;
    private DateTime _voiceLastSpeechAt;
    private VoiceModeWindow? _voiceWindow;
    private string _currentPage = "Chat";
    private bool _allowWindowClose;
    private readonly Border[] _speechMeters = new Border[8];
    private string ResourceDirectory => string.IsNullOrWhiteSpace(_config.ResourceDirectory) ? _root : _config.ResourceDirectory;
    private string SpeechModelsPath => Path.Combine(ResourceDirectory, "Models", "Speech");
    private CancellationTokenSource? _request;
    private Border? _activityPanel;
    private TextBlock? _activityTitle;
    private TextBlock? _activityDetail;
    private AppConfig _config = new();
#if JARVIS_BETA
    private string? _betaDisplayName;
    private string? _betaAvatarCachePath;
    private string? _betaDiscordId;
#endif
    private TaskExecutionPlan? _activePlan;
    private bool _loading;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const string DefaultPrompt = Jarvis.Services.DefaultSystemPrompt.Text;
    private string ConfigPath => Path.Combine(_root, "settings.json");
    private string TasksPath => Path.Combine(_root, "tasks.json");
    private string ChatsPath => Path.Combine(_root, "chats.json");
    private string MemoryPath => Path.Combine(_root, "memories.json");
    private string RemindersPath => Path.Combine(_root, "reminders.json");
    private string LogPath => Path.Combine(_root, "jarvis.log");

    public MainWindow()
    {
        InitializeComponent();
        _tts.StateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            UpdateTtsControls();
            if (!_voiceModeEnabled || !_voiceModeAwaitingTts) return;
            var german = _config.UiLanguage == "de";
            if (_tts.State == LocalTtsState.Speaking)
                _voiceWindow?.SetState(VoiceUiState.Speaking, german ? "Jarvis spricht…" : "Jarvis is speaking…", german ? "Du kannst Jarvis jederzeit unterbrechen." : "You can interrupt Jarvis at any time.");
        });
        _ttsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ttsTimer.Tick += (_, _) =>
        {
            _tts.RefreshState();
            if (_tts.State == LocalTtsState.Ready)
            {
                ResetTtsButton();
                if (_voiceModeAwaitingTts && _voiceModeEnabled && _request is null)
                {
                    StartVoiceListening();
                }
            }
        };
        _ttsTimer.Start();
        _voiceVadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _voiceVadTimer.Tick += (_, _) =>
        {
            // Leave a natural pause after each phrase so users can breathe or continue their sentence.
            if (_voiceModeEnabled && _speechBusy && !_speechTranscribing && _voiceSpeechDetected && !_voiceStopScheduled && DateTime.UtcNow - _voiceLastSpeechAt >= TimeSpan.FromMilliseconds(600))
            {
                _voiceStopScheduled = true;
                SpeechStop_Click(this, new RoutedEventArgs());
            }
        };
        _voiceVadTimer.Start();
        TaskList.ItemsSource = _tasks; MemoryList.ItemsSource = _memories; ReminderList.ItemsSource = _reminders;
        ChatList.ItemsSource = _chats; ChatTabs.ItemsSource = _openTabs;
        ChatTabs.PreviewMouseDown += ChatTabs_PreviewMouseDown; ChatList.PreviewMouseRightButtonDown += ChatList_PreviewMouseRightButtonDown;
        ReminderDatePicker.SelectedDate = DateTime.Today;
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; _reminderTimer.Tick += ReminderTimer_Tick; _reminderTimer.Start();
        _speechMeters[0] = Meter1; _speechMeters[1] = Meter2; _speechMeters[2] = Meter3; _speechMeters[3] = Meter4;
        _speechMeters[4] = Meter5; _speechMeters[5] = Meter6; _speechMeters[6] = Meter7; _speechMeters[7] = Meter8;
        _speechDeviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; _speechDeviceTimer.Tick += (_, _) => RefreshSpeechDevices(false); _speechDeviceTimer.Start();
        NavigateTo("Chat");
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try { var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle; var dark = 1; _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); }
        catch { }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        var configNeedsSave = false;
        try
        {
            Directory.CreateDirectory(_root);
            if (File.Exists(ConfigPath)) _config = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(ConfigPath), JsonOptions) ?? new();
            if (_config.UiLanguage is not ("de" or "en"))
            {
                _config.UiLanguage = Jarvis.Services.UiLocalization.DetectInitialLanguage();
                configNeedsSave = true;
            }
            AppVersionText.Text = (_config.UiLanguage == "de" ? "Lokale Desktop-KI · Version " : "Local desktop AI · Version ") + GetInstalledVersion();
            if (_config.UpdateChannel is not ("stable" or "beta"))
            {
#if JARVIS_BETA
                _config.UpdateChannel = "beta";
#else
                _config.UpdateChannel = "stable";
#endif
                configNeedsSave = true;
            }
            _config.TrustedApprovals ??= [];
            SpotifyClientIdBox.Text = _config.SpotifyClientId;
            SpotifyConnectionStatus.Text = SpotifyConnectionStatusText();
            AppleMusicStorefrontBox.Text = string.IsNullOrWhiteSpace(_config.AppleMusicStorefront) ? "de" : _config.AppleMusicStorefront;
            AppleMusicConnectionStatus.Text = AppleMusicConnectionStatusText();
            AppLanguageBox.SelectedValue = string.Equals(_config.UiLanguage, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
            UpdateChannelBox.SelectedValue = _config.UpdateChannel;
            if (!string.IsNullOrWhiteSpace(_config.ApiKeyProtected))
            {
                try
                {
                    var protectedBytes = Convert.FromBase64String(_config.ApiKeyProtected);
                    var apiKey = System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(apiKey));
                    CryptographicOperations.ZeroMemory(apiKey);
                }
                catch (Exception ex) { Log("settings-api-key", false, "Gespeicherter BionicGPT-Schlüssel konnte für dieses Windows-Konto nicht entschlüsselt werden: " + ex.Message); }
            }
            if (string.IsNullOrWhiteSpace(_config.SystemPrompt) || Jarvis.Services.DefaultSystemPrompt.IsLegacyDefault(_config.SystemPrompt)) { _config.SystemPrompt = DefaultPrompt; configNeedsSave = true; }
            if (_config.WakeWordEnabled && !_config.SpeechEnabled) { _config.SpeechEnabled = true; configNeedsSave = true; }
            SpeechEnabledCheck.IsChecked = _config.SpeechEnabled; SpeechUseDefaultCheck.IsChecked = _config.SpeechUseWindowsDefault;
            WakeWordEnabledCheck.IsChecked = _config.WakeWordEnabled;
            WakeWordPhraseBox.Text = string.IsNullOrWhiteSpace(_config.WakeWordPhrase) ? "Hey Jarvis" : _config.WakeWordPhrase;
            WakeSoundVolumeSlider.Value = Math.Clamp(_config.WakeSoundVolume, 0d, 1d) * 100d;
            VoiceEndSilenceSlider.Value = Math.Clamp(_config.VoiceEndSilenceMs, 350, 1500);
            SpeechModelBox.SelectedValue = _config.SpeechModel;
            TtsVoiceBox.ItemsSource = _tts.Voices;
            TtsPiperVoiceBox.ItemsSource = LocalTtsService.PiperVoicesForLanguage(_config.UiLanguage);
            TtsPiperVoiceBox.SelectedValue = string.IsNullOrWhiteSpace(_config.TtsPiperVoiceId) ? "de_DE-thorsten-high" : _config.TtsPiperVoiceId;
            TtsVolumeSlider.Value = Math.Clamp(_config.TtsVolume, 0d, 1d) * 100d;
            _tts.PiperVoiceId = _config.TtsPiperVoiceId;
            _tts.Volume = (float)_config.TtsVolume;
            TtsEngineBox.SelectedValue = _config.TtsEngine;
            _tts.Engine = _config.TtsEngine;
            _tts.ResourceDirectory = ResourceDirectory;
            _tts.PreferredVoiceName = _config.TtsVoiceName; _tts.PiperVoiceId = _config.TtsPiperVoiceId; _tts.Volume = (float)_config.TtsVolume;
            if (string.IsNullOrWhiteSpace(_config.TtsVoiceName)) TtsVoiceBox.SelectedIndex = 0;
            else TtsVoiceBox.SelectedValue = _config.TtsVoiceName;
            if (!_tts.Voices.Skip(1).Any(v => v.Name.Contains("Natural", StringComparison.OrdinalIgnoreCase) || v.Name.Contains("Neural", StringComparison.OrdinalIgnoreCase)))
                TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Auf diesem PC sind keine Windows-Natural-/Neural-Stimmen installiert. Die Auswahl hier zeigt die lokal vorhandenen Stimmen; natürlichere Stimmen können über Windows-Einstellungen → Zeit und Sprache → Sprachausgabe hinzugefügt werden." : "No Windows Natural/Neural voices are installed on this PC. The list shows voices available locally; more natural voices can be added in Windows Settings → Time & language → Speech.";
            else TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Wähle eine installierte Stimme. Natürlichere Natural-/Neural-Stimmen werden bevorzugt, wenn sie zur Sprache passen." : "Choose an installed voice. Natural/Neural voices are preferred when they match the language.";
            RefreshTtsModelStatus();
            EndpointBox.Text = _config.Endpoint; ModelBox.Text = _config.Model; ToolRootsList.ItemsSource = _config.AllowedRoots; DisplayNameBox.Text = _config.DisplayName; ProfileImagePathBox.Text = _config.ProfileImagePath; ApplyProfile();
            RefreshTrustedApprovals();
            SystemPromptBox.Text = _config.SystemPrompt; SaveChatCheck.IsChecked = _config.SaveChat;
            if (File.Exists(TasksPath)) foreach (var task in JsonSerializer.Deserialize<List<TaskRecord>>(await File.ReadAllTextAsync(TasksPath), JsonOptions) ?? []) _tasks.Add(task);
            if (File.Exists(MemoryPath)) foreach (var memory in JsonSerializer.Deserialize<List<MemoryRecord>>(await File.ReadAllTextAsync(MemoryPath), JsonOptions) ?? []) _memories.Add(memory);
            if (File.Exists(RemindersPath)) foreach (var reminder in JsonSerializer.Deserialize<List<ReminderRecord>>(await File.ReadAllTextAsync(RemindersPath), JsonOptions) ?? []) _reminders.Add(reminder);
            if (_config.SaveChat && File.Exists(ChatsPath)) foreach (var chat in await _chatStore.LoadAsync(ChatsPath)) _chats.Add(chat);
            var legacyChatPath = Path.Combine(_root, "chat.json");
            if (_chats.Count == 0 && _config.SaveChat && File.Exists(legacyChatPath))
            {
                var legacyMessages = JsonSerializer.Deserialize<List<ChatRecord>>(await File.ReadAllTextAsync(legacyChatPath), JsonOptions) ?? [];
                _chats.Add(new ChatSession { Title = legacyMessages.FirstOrDefault(m => m.Role == "user")?.Text is { Length: > 0 } title ? title[..Math.Min(42, title.Length)] : "Bisheriger Chat", Model = _config.Model, Messages = legacyMessages });
            }
            if (_chats.Count == 0) _chats.Add(new ChatSession { Model = _config.Model });
            var availableIds = _chats.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _config.OpenTabIds.Where(availableIds.Contains)) { var chat = _chats.First(c => c.Id == id); if (!_openTabs.Contains(chat)) _openTabs.Add(chat); }
            if (_openTabs.Count == 0) _openTabs.Add(_chats.FirstOrDefault(c => c.Id == _config.ActiveChatId) ?? _chats[0]);
            _activeChat = _openTabs.FirstOrDefault(c => c.Id == _config.ActiveChatId) ?? _openTabs[0];
            _loading = true; ChatList.SelectedItem = _activeChat; ChatTabs.SelectedItem = _activeChat; var activeModel = string.IsNullOrWhiteSpace(_activeChat.Model) ? _config.Model : _activeChat.Model; SetChatModel(activeModel); WorkspaceLabel.Text = string.IsNullOrWhiteSpace(_activeChat.Workspace) ? "Kein Ordner ausgewählt" : _activeChat.Workspace; RenderActiveChat(); _loading = false;
            RefreshMemorySummary(); ConnectionBadge.Text = _config.UiLanguage == "de" ? "Nicht getestet · lokal" : "Not tested · local";
            _loading = false;
            ApplyUiLanguage(_config.UiLanguage);
            if (configNeedsSave) await PersistConfig();
            await RefreshModelsAsync(false);
            RefreshSpeechDevices(true); UpdateSpeechModelStatus();
            if (_config.WakeWordEnabled) StartWakeWordMonitoring();
            RefreshWakeWordModelStatus();
            _ = CheckForUpdatesAsync(true);
        }
        catch (Exception ex) { _loading = false; StatusText.Text = "Lokale Daten konnten nicht geladen werden"; Log("startup", false, ex.Message); }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var page = (sender as Button)?.Tag?.ToString() ?? "Chat";
        NavigateTo(page);
    }

    private async void VoiceMode_Click(object sender, RoutedEventArgs e)
    {
        var isGerman = string.Equals(_config.UiLanguage, "de", StringComparison.OrdinalIgnoreCase);
        if (_voiceModeEnabled)
        {
            if (_voiceWindow is { IsVisible: true })
            {
                if (_voiceWindow.WindowState == WindowState.Minimized) _voiceWindow.WindowState = WindowState.Normal;
                _voiceWindow.Activate();
            }
            return;
        }
        _voiceModeEnabled = true; _voiceModeAwaitingTts = false; NavigateTo("Chat");
        _voiceWindow = new VoiceModeWindow { Owner = this };
        _voiceWindow.SetLanguage(_config.UiLanguage);
        _voiceWindow.MuteChanged += muted => { _speech.SetMuted(muted); _voiceMuted = muted; };
        _speech.SetMuted(_voiceMuted);
        _voiceWindow.RetryRequested += (_, _) => { if (_voiceModeEnabled && !_speechBusy && _request is null) { _voiceWindow.SetState(VoiceUiState.Listening, isGerman ? "Jarvis hört zu…" : "Listening…", isGerman ? "Sprich ganz normal. Nach einer kurzen Pause antwortet Jarvis." : "Speak naturally. Jarvis will respond after a short pause."); Mic_Click(this, new RoutedEventArgs()); } };
        _voiceWindow.EndRequested += (_, _) => StopVoiceMode();
        _voiceWindow.Closed += (_, _) => StopVoiceMode();
        _voiceWindow.Show();
        UpdateVoiceModeNavigation();
        _voiceWindow.SetState(VoiceUiState.Thinking, "Preparing speech recognition…", "Warming up your local speech model for faster replies.");
        _voicePreparation?.Cancel();
        var preparation = new CancellationTokenSource();
        _voicePreparation = preparation;
        try
        {
            await StopWakeWordMonitoringAsync();
            await Task.WhenAll(
                _speech.PreloadModelAsync(SpeechModelPath(), preparation.Token),
                _tts.PreloadSelectedVoiceAsync(preparation.Token),
                _wakeWordTtsWarmup,
                _wakeWordSpeechWarmup);
            if (!_voiceModeEnabled || preparation.IsCancellationRequested) return;
            _voiceWindow.SetState(VoiceUiState.Listening, isGerman ? "Jarvis hört zu…" : "Listening…", isGerman ? "Sprich ganz normal. Nach einer kurzen Pause antwortet Jarvis." : "Speak naturally. Jarvis will respond after a short pause.");
            Mic_Click(sender, e);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _voiceWindow?.SetState(VoiceUiState.Error, "Voice mode could not start", _config.UiLanguage == "de" ? ex.Message : "Voice components could not be prepared. Check Speech input and Speech output in Settings.");
            Log("voice-model-warmup", false, ex.ToString());
        }
        finally
        {
            if (ReferenceEquals(_voicePreparation, preparation)) _voicePreparation = null;
            preparation.Dispose();
        }
    }

    private void WakeWordButton_Click(object sender, RoutedEventArgs e)
    {
        WakeWordEnabledCheck.IsChecked = !(_config.WakeWordEnabled);
    }

    private async void WakeWordSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || WakeWordEnabledCheck is null) return;
        _config.WakeWordEnabled = WakeWordEnabledCheck.IsChecked == true;
        _config.WakeWordPhrase = string.IsNullOrWhiteSpace(WakeWordPhraseBox.Text) ? "Hey Jarvis" : WakeWordPhraseBox.Text.Trim();
        _config.WakeSoundVolume = Math.Clamp(WakeSoundVolumeSlider.Value / 100d, 0d, 1d);
        _config.VoiceEndSilenceMs = (int)Math.Clamp(VoiceEndSilenceSlider.Value, 350d, 1500d);
        // Enabling the always-listening wake word is an explicit user action, so
        // make sure speech input itself is enabled as well. Previously the button
        // could show "on" while the listener immediately returned without running.
        if (_config.WakeWordEnabled && SpeechEnabledCheck.IsChecked != true)
            SpeechEnabledCheck.IsChecked = true;
        _config.SpeechEnabled = SpeechEnabledCheck.IsChecked == true;
        UpdateWakeWordButton();
        try { await PersistConfig(); }
        catch (Exception ex) { Log("wake-word-settings", false, ex.ToString()); }
        if (_config.WakeWordEnabled) StartWakeWordMonitoring();
        else await StopWakeWordMonitoringAsync();
        RefreshWakeWordModelStatus();
    }

    private async void WakeWordModelDownload_Click(object sender, RoutedEventArgs e)
    {
        WakeWordModelDownloadButton.IsEnabled = false;
        WakeWordModelProgress.Visibility = Visibility.Visible;
        if (_wakeWordListening) await StopWakeWordMonitoringAsync();
        try
        {
            var progress = new Progress<string>(message => WakeWordModelStatus.Text = message);
            await LocalWakeWordService.InstallModelAsync(ResourceDirectory, progress);
            Log("wake-word-model", true, "Local Sherpa-ONNX keyword-spotting model installed.");
        }
        catch (Exception ex)
        {
            WakeWordModelStatus.Text = (_config.UiLanguage == "de" ? "Download fehlgeschlagen: " : "Download failed: ") + ex.Message;
            Log("wake-word-model", false, ex.ToString());
        }
        finally
        {
            WakeWordModelProgress.Visibility = Visibility.Collapsed;
            WakeWordModelDownloadButton.IsEnabled = true;
            UpdateWakeWordModelStatus();
            if (_config.WakeWordEnabled && LocalWakeWordService.IsModelInstalled(ResourceDirectory)) StartWakeWordMonitoring();
        }
    }

    private void RefreshWakeWordModelStatus() => UpdateWakeWordModelStatus();

    private void UpdateWakeWordModelStatus()
    {
        if (WakeWordModelStatus is null || WakeWordModelDownloadButton is null) return;
        var installed = LocalWakeWordService.IsModelInstalled(ResourceDirectory);
        var engineStatus = _wakeWordListening
            ? (_config.UiLanguage == "de" ? "Mikrofon aktiv · höre auf „" + _config.WakeWordPhrase + "“." : "Microphone active · listening for “" + _config.WakeWordPhrase + "”.")
            : (_config.WakeWordEnabled
                ? (_config.UiLanguage == "de" ? "Erkennung ist eingeschaltet, startet aber noch oder meldet einen Fehler unten in der Statusleiste." : "Detection is enabled but still starting or has an error in the status bar.")
                : (_config.UiLanguage == "de" ? "Erkennung ist ausgeschaltet." : "Detection is off."));
        WakeWordModelStatus.Text = (installed
            ? (_config.UiLanguage == "de" ? "Modell bereit · lokal und offline. Das schnelle Sherpa-Modell ist auf englische Aussprache trainiert." : "Model ready · local and offline. The fast Sherpa model is trained for English pronunciation.")
            : (_config.UiLanguage == "de" ? "Modell fehlt · einmalig ca. 19 MB herunterladen; danach offline nutzbar." : "Model missing · download about 19 MB once; then it works offline."))
            + Environment.NewLine + engineStatus;
        WakeWordModelDownloadButton.Content = installed
            ? (_config.UiLanguage == "de" ? "Aktivierungsmodell prüfen" : "Check wake-word model")
            : (_config.UiLanguage == "de" ? "Aktivierungsmodell herunterladen" : "Download wake-word model");
    }

    private async Task WarmSpeechForWakeWordAsync()
    {
        try { await _speech.PreloadModelAsync(SpeechModelPath()); }
        catch (Exception ex) { Log("wake-word-speech-warmup", false, ex.ToString()); }
    }

    private void StartWakeWordMonitoring()
    {
        if (!_config.WakeWordEnabled || _voiceModeEnabled || _wakeWordTask is { IsCompleted: false }) return;
        if (SpeechEnabledCheck.IsChecked != true)
        {
            _wakeWordListening = false;
            UpdateWakeWordButton();
            StatusText.Text = "Wake word pausiert · Spracheingabe ist deaktiviert";
            return;
        }
        var preferredDeviceId = SpeechDeviceBox.SelectedValue?.ToString() ?? "";
        if (SpeechUseDefaultCheck.IsChecked != true && _speech.GetDevices().All(d => !string.Equals(d.Id, preferredDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            _wakeWordListening = false;
            UpdateWakeWordButton();
            StatusText.Text = _config.UiLanguage == "de"
                ? "Hey Jarvis kann das ausgewählte Mikrofon nicht finden · Einstellungen → Spracheingabe"
                : "Hey Jarvis cannot find the selected microphone · Settings → Speech input";
            Log("wake-word-listener", false, "Selected microphone is unavailable.");
            return;
        }
        _wakeWordCancellation?.Dispose();
        _wakeWordCancellation = new CancellationTokenSource();
        _wakeWordListening = true;
        UpdateWakeWordButton();
        // Load STT and TTS while the keyword detector is idle-listening, so the
        // user doesn't have to wait for model initialization after saying the phrase.
        _wakeWordSpeechWarmup = WarmSpeechForWakeWordAsync();
        _wakeWordTtsWarmup = WarmTtsForWakeWordAsync();
        var token = _wakeWordCancellation.Token;
        StatusText.Text = _config.UiLanguage == "de" ? "Hey Jarvis · Wake-Word-Modell wird vorbereitet …" : "Hey Jarvis · preparing wake-word model…";
        _wakeWordTask = PrepareAndRunWakeWordLoopAsync(token);
    }

    private async Task WarmTtsForWakeWordAsync()
    {
        try { await _tts.PreloadSelectedVoiceAsync(); }
        catch (Exception ex) { Log("wake-word-tts-warmup", false, ex.ToString()); }
    }

    private async Task PrepareAndRunWakeWordLoopAsync(CancellationToken token)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            await WakeWordLoopAsync(token);
            timer.Stop();
            Log("wake-word-listener", true, $"Real-time sherpa-onnx keyword spotting stopped after {timer.Elapsed.TotalSeconds:F1}s");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("wake-word-listener", false, ex.ToString());
            await Dispatcher.InvokeAsync(() =>
            {
                _wakeWordListening = false;
                StatusText.Text = (_config.UiLanguage == "de"
                    ? "Hey Jarvis konnte den Wake-Word-Detektor nicht starten · "
                    : "Hey Jarvis could not start its wake-word detector · ") + ex.Message;
                UpdateWakeWordButton();
            });
        }
    }

    private async Task WakeWordLoopAsync(CancellationToken token)
    {
        var selectedId = SpeechDeviceBox.SelectedValue?.ToString();
        var useDefault = SpeechUseDefaultCheck.IsChecked == true;
        var progress = new Progress<string>(message =>
        {
            StatusText.Text = message;
            if (WakeWordModelStatus is not null)
                WakeWordModelStatus.Text = message;
            Log("wake-word-status", true, message);
        });
        using var detector = new LocalWakeWordService();
        await detector.ListenAsync(selectedId, useDefault, _config.WakeWordPhrase, ResourceDirectory, token, (keyword, preRollAudio, preRollFormat) =>
        {
            _wakeWordPrefillAudio = preRollAudio;
            _wakeWordPrefillFormat = preRollFormat;
            _wakeWordPrefillPending = preRollAudio.Length > 0;
            Log("wake-word-detected", true, $"keyword={keyword}; engine=Sherpa-ONNX streaming KWS; preroll_bytes={preRollAudio.Length}");
            _ = Dispatcher.BeginInvoke(new Action(BeginVoiceFromWakeWord), DispatcherPriority.Normal);
        }, progress);
    }

    private static string NormalizeWakePhrase(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static bool WakePhraseMatches(string recognizedText, string configuredPhrase)
    {
        var recognized = NormalizeWakePhrase(recognizedText);
        var phrase = NormalizeWakePhrase(configuredPhrase);
        if (recognized.Length == 0 || phrase.Length == 0) return false;
        // Whisper's detected variant from the supplied recording. Accept it even
        // when the user changed the display phrase, as the trigger remains Jarvis.
        if (recognized.Contains("chavez", StringComparison.Ordinal)
            || recognized.Contains("chaves", StringComparison.Ordinal)) return true;
        if (recognized.Contains(phrase, StringComparison.Ordinal)) return true;

        // Whisper often omits the short “Hey” at the beginning of a clipped window,
        // or transcribes the name as “Jervis”. For the default phrase, accept the
        // distinctive name as well, with one-character transcription tolerance.
        if (phrase == "hey jarvis")
        {
            // Whisper's German/English auto-detection repeatedly renders the spoken
            // name as “Chavez” (visible in the user's diagnostic screenshot).
            // Match the full phrase explicitly before token-level fallbacks.
            if (recognized.Contains("hey chavez", StringComparison.Ordinal)
                || recognized.Contains("hey chaves", StringComparison.Ordinal)
                || recognized.Contains("hey javes", StringComparison.Ordinal)) return true;

            var words = recognized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < words.Length; index++)
            {
                var word = words[index];
                if (word is "jarvis" or "jervis" or "jarwis" or "jarviss" or "jarvish" or "jarwes" or "chaves" or "chavez" or "shaves" or "javes" || IsWithinOneEdit(word, "jarvis")) return true;

                // Whisper may cut the last syllable at a short-window boundary.
                // Accept a clearly Jarvis-like partial token only when it follows
                // the spoken wake prefix, never from unrelated speech by itself.
                var previousIsHey = index > 0 && (words[index - 1] is "hey" or "hei" or "hay");
                if (previousIsHey && (word.Length >= 3 && word.StartsWith("jar", StringComparison.Ordinal) || IsWithinTwoEdits(word, "jarvis") || word is "chaves" or "chavez" or "shaves" or "javes")) return true;
            }
        }
        return false;
    }

    private static bool IsWithinOneEdit(string value, string target) => IsWithinEdits(value, target, 1);

    private static bool IsWithinTwoEdits(string value, string target) => IsWithinEdits(value, target, 2);

    private static bool IsWithinEdits(string value, string target, int maxEdits)
    {
        if (Math.Abs(value.Length - target.Length) > maxEdits) return false;
        var previous = Enumerable.Range(0, target.Length + 1).ToArray();
        for (var i = 1; i <= value.Length; i++)
        {
            var current = new int[target.Length + 1];
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (value[i - 1] == target[j - 1] ? 0 : 1));
            if (current.Min() > maxEdits) return false;
            previous = current;
        }
        return previous[target.Length] <= maxEdits;
    }

    private async void BeginVoiceFromWakeWord()
    {
        if (_voiceModeEnabled || !_config.WakeWordEnabled) return;
        try
        {
            PlayWakeSound();
            StatusText.Text = _config.UiLanguage == "de" ? "Hey Jarvis erkannt · ich höre zu" : "Hey Jarvis detected · listening";
            await CreateChatAsync();
            VoiceMode_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            StatusText.Text = _config.UiLanguage == "de"
                ? "Sprachmodus konnte nicht gestartet werden: " + ex.Message
                : "Voice mode could not start. Check Speech input in Settings.";
            Log("wake-word-activation", false, ex.ToString());
            StartWakeWordMonitoring();
        }
    }

    private async Task StopWakeWordMonitoringAsync()
    {
        _wakeWordCancellation?.Cancel();
        var task = _wakeWordTask;
        if (task is not null)
        {
            try { await task; } catch { }
        }
        _wakeWordListening = false;
        _wakeWordTask = null;
        _wakeWordCancellation?.Dispose();
        _wakeWordCancellation = null;
        UpdateWakeWordButton();
    }

    private void UpdateWakeWordButton()
    {
        if (WakeWordButton is null) return;
        var german = _config.UiLanguage == "de";
        WakeWordButton.Content = _config.WakeWordEnabled
            ? (german ? (_wakeWordListening ? "◉  Hey Jarvis · hört zu" : "◉  Hey Jarvis · an") : (_wakeWordListening ? "◉  Hey Jarvis · listening" : "◉  Hey Jarvis · on"))
            : (german ? "◉  Hey Jarvis · aus" : "◉  Hey Jarvis · off");
        WakeWordButton.ToolTip = german
            ? "Aktiviert die lokale Wake-Word-Erkennung im Hintergrund. Das Mikrofon bleibt dabei aktiv."
            : "Enables local wake-word detection in the background. The microphone stays active.";
        WakeWordButton.Background = _config.WakeWordEnabled ? new SolidColorBrush(Color.FromRgb(48, 42, 85)) : new SolidColorBrush(Color.FromRgb(27, 28, 38));
    }

    private void StopVoiceMode()
    {
        // The window's End button raises EndRequested and Close() also raises Closed.
        // Make the second notification harmless and refresh after both routed events finish.
        if (!_voiceModeEnabled && _voiceWindow is null)
        {
            UpdateVoiceModeNavigation();
            return;
        }
        _voiceModeEnabled = false; _voiceModeAwaitingTts = false; _voicePreparation?.Cancel(); _voiceVadTimer.Stop(); _request?.Cancel();
        UpdateVoiceModeNavigation();
        if (_speechBusy) SpeechCancel_Click(this, new RoutedEventArgs());
        _tts.Stop();
        var window = _voiceWindow; _voiceWindow = null;
        if (window is not null && window.IsVisible) window.Close();
        _voiceVadTimer.Start();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, UpdateVoiceModeNavigation);
        if (_config.WakeWordEnabled) _ = ResumeWakeWordMonitoringAsync();
    }

    private async Task ResumeWakeWordMonitoringAsync()
    {
        while (_config.WakeWordEnabled && !_allowWindowClose && _speechBusy) await Task.Delay(400);
        if (_config.WakeWordEnabled && !_voiceModeEnabled && !_allowWindowClose) StartWakeWordMonitoring();
    }

    private void UpdateVoiceModeNavigation()
    {
        if (NavVoiceMode is null) return;
        var german = _config.UiLanguage == "de";
        NavVoiceMode.Content = _voiceModeEnabled
            ? (german ? "◖))   Sprachmodus beenden" : "◖))   End voice mode")
            : (german ? "◖))   Sprachmodus" : "◖))   Voice mode");
        NavVoiceMode.ToolTip = _voiceModeEnabled
            ? (german ? "Sprachmodus beenden" : "End voice mode")
            : (german ? "Spracheingabe und gesprochene Jarvis-Antwort aktivieren" : "Start speech input and spoken Jarvis replies");
    }

    private async Task SpeakVoiceReplyAsync(string text)
    {
        if (!_voiceModeEnabled) return;
        _voiceWindow?.SetResponse(text);
        _voiceWindow?.SetState(VoiceUiState.Speaking, "Jarvis is speaking…", "Jarvis will listen again when the response finishes.");
        // Set this before synthesis starts. The TTS service exposes a Generating state,
        // so the watcher only resumes recording after actual playback has finished.
        _voiceModeAwaitingTts = true;
        StartVoiceBargeInCapture();
        try { await _tts.SpeakAsync(text, _config.UiLanguage); }
        catch { _voiceModeAwaitingTts = false; throw; }
        finally
        {
            // A normal finish discards the microphone monitor (which may contain
            // speaker echo) and starts a clean listening capture. A barge-in keeps
            // the current recording so the user's speech can be transcribed.
            if (_voiceBargeInMonitoring) await StopVoiceBargeInCaptureAsync();
        }
        if (_voiceModeEnabled && _tts.State == LocalTtsState.Ready) StartVoiceListening();
    }

    private async Task<bool> SpeakVoiceReplyStreamAsync(ChannelReader<string> textChunks, CancellationToken cancellationToken)
    {
        if (!_voiceModeEnabled) return false;
        _voiceWindow?.SetResponse("");
        _voiceWindow?.SetState(VoiceUiState.Thinking, "Jarvis is preparing a reply…", "The first complete sentence will be spoken as soon as it is ready.");
        _voiceModeAwaitingTts = true;
        StartVoiceBargeInCapture();
        try
        {
            var spoke = await _tts.SpeakStreamAsync(textChunks, _config.UiLanguage, cancellationToken);
            return spoke;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _tts.Stop();
            return false;
        }
        finally
        {
            if (_voiceBargeInMonitoring) await StopVoiceBargeInCaptureAsync();
            _voiceModeAwaitingTts = false;
        }
    }

    private void StartVoiceBargeInCapture()
    {
        if (!_voiceModeEnabled || _speechBusy || _voiceMuted || SpeechEnabledCheck.IsChecked != true || !File.Exists(SpeechModelPath())) return;
        if (SpeechUseDefaultCheck.IsChecked != true && SpeechDeviceBox.SelectedItem is not SpeechDevice) return;
        try
        {
            var path = Path.Combine(SpeechModelsPath, $"barge-{Guid.NewGuid():N}.wav");
            _speechLastCapturePath = path;
            _speechTranscribing = false;
            _voiceSpeechDetected = false;
            _voiceStopScheduled = false;
            _voiceBargeInCandidateSince = DateTime.MinValue;
            _speech.StartRecording(SpeechDeviceBox.SelectedValue?.ToString(), SpeechUseDefaultCheck.IsChecked == true, path,
                level => Dispatcher.BeginInvoke(() => OnSpeechLevel(level)));
            _speechBusy = true;
            _speechOperation = new CancellationTokenSource();
            _speechCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _voiceBargeInMonitoring = true;
            Log("voice-barge-in", true, "Mikrofon überwacht Sprache während TTS");
        }
        catch (Exception ex)
        {
            _voiceBargeInMonitoring = false;
            _speechBusy = false;
            Log("voice-barge-in", false, ex.ToString());
        }
    }

    private async Task StopVoiceBargeInCaptureAsync()
    {
        if (!_voiceBargeInMonitoring) return;
        _voiceBargeInMonitoring = false;
        var path = _speechLastCapturePath;
        var operation = _speechOperation;
        var completion = _speechCompletion;
        _speech.CancelRecording();
        await _speech.WaitForRecordingStopAsync();
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        if (ReferenceEquals(_speechOperation, operation))
        {
            _speechBusy = false;
            _speechTranscribing = false;
            _speechOperation?.Dispose();
            _speechOperation = null;
            _speechLastCapturePath = "";
            completion?.TrySetResult();
        }
    }

    private void BeginVoiceBargeIn()
    {
        if (!_voiceBargeInMonitoring || !_voiceModeEnabled) return;
        try { _speech.ResetRecordingBuffer(_speechLastCapturePath); }
        catch (Exception ex)
        {
            Log("voice-barge-in", false, "Could not reset capture buffer: " + ex);
            return;
        }
        _voiceBargeInMonitoring = false;
        _voiceBargeInCandidateSince = DateTime.MinValue;
        _voiceSpeechDetected = true;
        _voiceLastSpeechAt = DateTime.UtcNow;
        _voiceStopScheduled = false;
        _voiceModeAwaitingTts = false;
        _request?.Cancel();
        _tts.Stop();
        _voiceWindow?.SetState(VoiceUiState.Listening, "Listening…", "I stopped speaking because you started talking.");
        StatusText.Text = _config.UiLanguage == "de" ? "Sprachmodus · Jarvis hört zu" : "Voice mode · listening";
        Log("voice-barge-in", true, "TTS durch erkannte Nutzersprache unterbrochen");
    }

    private void StartVoiceListening()
    {
        if (!_voiceModeEnabled || _speechBusy || _request is not null) return;
        _voiceModeAwaitingTts = false;
        var german = _config.UiLanguage == "de";
        _voiceWindow?.SetState(VoiceUiState.Listening, german ? "Jarvis hört zu…" : "Listening…", german ? "Sprich ganz normal. Jarvis reagiert nach einer kurzen Pause." : "Speak naturally. Jarvis responds after a short pause.");
        Mic_Click(this, new RoutedEventArgs());
    }

    private void NavigateTo(string page)
    {
        _currentPage = page;
        ChatTabs.Visibility = page == "Chat" ? Visibility.Visible : Visibility.Collapsed;
        PageHeader.Visibility = page == "Chat" ? Visibility.Collapsed : Visibility.Visible;
        ChatPage.Visibility = page == "Chat" ? Visibility.Visible : Visibility.Collapsed;
        AgentPage.Visibility = page == "Agenten" ? Visibility.Visible : Visibility.Collapsed;
        ToolsPage.Visibility = page == "Werkzeuge" ? Visibility.Visible : Visibility.Collapsed;
        TasksPage.Visibility = page == "Aufgaben" ? Visibility.Visible : Visibility.Collapsed;
        RemindersPage.Visibility = page == "Erinnerungen" ? Visibility.Visible : Visibility.Collapsed;
        MemoryPage.Visibility = page == "Memory" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Einstellungen" ? Visibility.Visible : Visibility.Collapsed;
        NavChat.Background = page == "Chat" ? new SolidColorBrush(Color.FromRgb(43, 41, 57)) : System.Windows.Media.Brushes.Transparent;
        NavChat.Foreground = (Brush)FindResource(page == "Chat" ? "Accent" : "Text");
        var pageTitle = page == "Memory" ? "Was Jarvis über mich weiß" : page;
        var subtitle = page switch { "Chat" => "Dein privater Arbeitsbereich", "Agenten" => "Status der Aufgabenplanung", "Werkzeuge" => "Begrenzte, direkt ausgelöste Windows-Aktionen", "Aufgaben" => "Nachvollziehbare lokale Historie", "Erinnerungen" => "Noch nicht verfügbar", "Memory" => "Lokal gespeicherte und von dir kontrollierte Informationen", _ => "Verbindung, Systemprompt und Datenschutz" };
        PageTitle.Text = Jarvis.Services.UiLocalization.Translate(pageTitle, _config.UiLanguage);
        PageSubtitle.Text = Jarvis.Services.UiLocalization.Translate(subtitle, _config.UiLanguage);
    }

    private async void AppLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppLanguageBox.SelectedValue is not string language) return;
        _config.UiLanguage = language;
        ApplyUiLanguage(language);
        var selectedPiperVoice = TtsPiperVoiceBox.SelectedValue?.ToString();
        TtsPiperVoiceBox.ItemsSource = LocalTtsService.PiperVoicesForLanguage(language);
        TtsPiperVoiceBox.SelectedValue = selectedPiperVoice;
        RefreshTtsModelStatus();
        try { await PersistConfig(); SettingsStatus.Text = language == "de" ? "Sprache gespeichert." : "Language saved."; }
        catch (Exception ex) { SettingsStatus.Text = (language == "de" ? "Sprache konnte nicht gespeichert werden: " : "Could not save language: ") + ex.Message; }
    }

    private void ApplyUiLanguage(string language)
    {
        Jarvis.Services.UiLocalization.Apply(this, language, Messages);
        // Status labels are populated asynchronously outside the XAML resource tree.
        SpotifyConnectionStatus.Text = SpotifyConnectionStatusText();
        AppleMusicConnectionStatus.Text = AppleMusicConnectionStatusText();
        ConnectionBadge.Text = Jarvis.Services.UiLocalization.Translate(ConnectionBadge.Text, language);
        if (_modelDiscoveryFailed)
        {
            ModelStatus.Text = language == "de" ? "Kein Server verbunden · Modell manuell wählbar" : "No server connected · model can be entered manually";
            if (!string.IsNullOrWhiteSpace(_modelDiscoveryError))
                ModelsResult.Text = _modelDiscoveryError + (language == "de" ? "\nModellnamen kannst du weiterhin manuell eintragen." : "\nYou can still enter a model name manually.");
        }
        else if (_availableModels.Count > 0)
        {
            var loaded = _availableModels.Count(option => option.IsLoaded);
            ModelStatus.Text = language == "de" ? $"{_availableModels.Count} Chatmodell(e) · {loaded} geladen · andere laden beim Senden" : $"{_availableModels.Count} chat model(s) · {loaded} loaded · others load when sent";
            if (!string.IsNullOrWhiteSpace(ModelsResult.Text) && ModelsResult.Text != (language == "de" ? "Modelle werden geladen …" : "Loading models…"))
                ModelsResult.Text = language == "de" ? $"{_availableModels.Count} Chatmodell(e) aus {_modelsSource}. Geladene: {loaded}; weitere Modelle werden beim Senden geladen, wenn der Server das unterstützt." : $"{_availableModels.Count} chat model(s) from {_modelsSource}. Loaded: {loaded}; other models will load when sent if supported by the server.";
        }
        else
        {
            ModelStatus.Text = Jarvis.Services.UiLocalization.Translate(ModelStatus.Text, language);
            ModelsResult.Text = Jarvis.Services.UiLocalization.Translate(ModelsResult.Text, language);
        }
        PageTitle.Text = Jarvis.Services.UiLocalization.Translate(_currentPage == "Memory" ? "Was Jarvis über mich weiß" : _currentPage, language);
        var subtitle = _currentPage switch { "Chat" => "Dein privater Arbeitsbereich", "Agenten" => "Status der Aufgabenplanung", "Werkzeuge" => "Begrenzte, direkt ausgelöste Windows-Aktionen", "Aufgaben" => "Nachvollziehbare lokale Historie", "Erinnerungen" => "Noch nicht verfügbar", "Memory" => "Lokal gespeicherte und von dir kontrollierte Informationen", _ => "Verbindung, Systemprompt und Datenschutz" };
        PageSubtitle.Text = Jarvis.Services.UiLocalization.Translate(subtitle, language);
        NavChat.Content = "Chat";
        UpdateVoiceModeNavigation();
        ProfileType.Text = language == "de" ? "Lokaler Benutzer" : "Local user";
        if (UpdateCheckButton is not null) UpdateCheckButton.Content = language == "de" ? "Nach Updates suchen" : "Check for updates";
        if (UpdateChannelLabel is not null) UpdateChannelLabel.Text = language == "de" ? "Release-Kanal" : "Release channel";
        if (UpdateChannelBox?.Items.Count >= 2)
        {
            ((ComboBoxItem)UpdateChannelBox.Items[0]).Content = language == "de" ? "Stabil" : "Stable";
            ((ComboBoxItem)UpdateChannelBox.Items[1]).Content = language == "de" ? "Beta / Vorabversion" : "Beta / prerelease";
        }
        if (UpdateStatusText is not null && string.IsNullOrWhiteSpace(UpdateStatusText.Text)) UpdateStatusText.Text = language == "de" ? "Automatische Prüfung beim Start." : "Updates are checked automatically at startup.";
        _voiceWindow?.SetLanguage(language);
        UpdateWakeWordButton();
        UpdateWakeWordModelStatus();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(false);

    private async Task CheckForUpdatesAsync(bool automatic)
    {
        if ((automatic && _automaticUpdatePromptShown) || _updateCheckRunning) return;
        _updateCheckRunning = true;
        if (UpdateStatusText is not null) UpdateStatusText.Text = _config.UiLanguage == "de" ? "Suche nach Updates …" : "Checking for updates…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var releases = await _updates.GetReleasesAsync(timeout.Token);
            if (!SemanticVersion.TryParse(GetInstalledVersion(), out var installed) || installed is null) throw new InvalidDataException("Installed version metadata is invalid.");
            var release = UpdateSecurity.SelectEligible(releases, installed, _config.UpdateChannel);
            if (release is null)
            {
                if (UpdateStatusText is not null) UpdateStatusText.Text = _config.UiLanguage == "de" ? "Jarvis ist auf dem neuesten Stand." : "Jarvis is up to date.";
                return;
            }
            var signedPackage = await _updates.GetSignedPackageAsync(release, timeout.Token);
            if (UpdateStatusText is not null) UpdateStatusText.Text = signedPackage is null
                ? (_config.UiLanguage == "de" ? $"Version {release.Version} verfügbar, aber das Paket ist nicht verifiziert. Öffne die Release-Seite." : $"Version {release.Version} is available, but its package could not be verified. Open the release page.")
                : (_config.UiLanguage == "de" ? $"Version {release.Version} verfügbar." : $"Version {release.Version} is available.");
            if (!automatic || _automaticUpdatePromptShown)
            {
                if (!automatic) ShowUpdateDialog(release, signedPackage);
                return;
            }
            _automaticUpdatePromptShown = true;
            ShowUpdateDialog(release, signedPackage);
        }
        catch (OperationCanceledException)
        {
            if (UpdateStatusText is not null) UpdateStatusText.Text = _config.UiLanguage == "de" ? "Update-Prüfung hat zu lange gedauert." : "The update check timed out.";
            if (!automatic) ShowUpdateError(_config.UiLanguage == "de" ? "Die Update-Prüfung hat das Zeitlimit überschritten." : "The update check timed out.");
        }
        catch (Exception ex)
        {
            Log("update-check", false, ex.ToString());
            if (UpdateStatusText is not null) UpdateStatusText.Text = _config.UiLanguage == "de" ? "Updates derzeit nicht erreichbar." : "Updates are currently unavailable.";
            if (!automatic) ShowUpdateError(_config.UiLanguage == "de" ? "GitHub ist derzeit nicht erreichbar. Jarvis kann normal weiterverwendet werden." : "GitHub is currently unavailable. Jarvis can continue running normally.");
        }
        finally { _updateCheckRunning = false; }
    }

    private void ShowUpdateDialog(GitHubRelease release, UpdatePackage? package)
    {
        if (_lastPromptedUpdateVersion == release.Version.ToString()) return;
        _lastPromptedUpdateVersion = release.Version.ToString();
        var dialog = new JarvisUpdateDialog(GetInstalledVersion(), release, package is not null, _config.UiLanguage) { Owner = this };
        dialog.ViewRelease += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(release.HtmlUri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { ShowUpdateError(_config.UiLanguage == "de" ? "Die Release-Seite konnte nicht geöffnet werden." : "Could not open the release page."); Log("update-release-page", false, ex.ToString()); }
        };
        dialog.InstallRequested += async (_, _) => await DownloadAndInstallUpdateAsync(package!, dialog);
        dialog.ShowDialog();
    }

    private async Task DownloadAndInstallUpdateAsync(UpdatePackage package, JarvisUpdateDialog dialog)
    {
        var temp = Path.Combine(Path.GetTempPath(), "JarvisUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var archive = Path.Combine(temp, UpdateSecurity.PackageName); var manifestPath = Path.Combine(temp, UpdateSecurity.ManifestName); var signaturePath = Path.Combine(temp, UpdateSecurity.SignatureName);
        try
        {
            dialog.SetBusy(true, _config.UiLanguage == "de" ? "Update wird heruntergeladen …" : "Downloading update…");
            var progress = new Progress<(long Received, long? Total)>(p => dialog.SetProgress(p.Received, p.Total));
            await _updates.DownloadPackageAsync(package, archive, progress);
            await File.WriteAllBytesAsync(manifestPath, package.ManifestBytes); await File.WriteAllTextAsync(signaturePath, package.Signature, Encoding.ASCII);
            var helper = Path.Combine(AppContext.BaseDirectory, "Jarvis.Updater.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException(_config.UiLanguage == "de" ? "Der sichere Update-Helfer fehlt in dieser Installation. Bitte verwende die Release-Seite." : "The secure update helper is missing from this installation. Use the release page instead.", helper);
            var runnableHelper = Path.Combine(temp, "Jarvis.Updater.exe"); File.Copy(helper, runnableHelper, true);
            var start = new ProcessStartInfo(runnableHelper) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var arg in new[] { "--pid", Environment.ProcessId.ToString(), "--install", AppContext.BaseDirectory, "--package", archive, "--manifest", manifestPath, "--signature", signaturePath, "--language", _config.UiLanguage }) start.ArgumentList.Add(arg);
            if (Process.Start(start) is null) throw new InvalidOperationException("Could not start the update helper.");
            _allowWindowClose = true; dialog.Close(); Close();
        }
        catch (Exception ex)
        {
            Log("update-install", false, ex.ToString());
            dialog.SetBusy(false, (_config.UiLanguage == "de" ? "Update fehlgeschlagen: " : "Update failed: ") + ex.Message);
        }
    }

    private void ShowUpdateError(string message) => MessageBox.Show(this, message, "Jarvis", MessageBoxButton.OK, MessageBoxImage.Information);
    private static string GetInstalledVersion() => System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private async void UpdateChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UpdateChannelBox?.SelectedValue is not string channel) return;
        _config.UpdateChannel = channel;
        try { await PersistConfig(); UpdateStatusText.Text = _config.UiLanguage == "de" ? "Release-Kanal gespeichert." : "Release channel saved."; }
        catch (Exception ex) { Log("update-channel", false, ex.ToString()); UpdateStatusText.Text = _config.UiLanguage == "de" ? "Release-Kanal konnte nicht gespeichert werden." : "Could not save release channel."; }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e) => await CreateChatAsync();
    private async Task CreateChatAsync()
    {
        if (_activeChat is not null) { _activeChat.Messages = ReadChat(); _draftAttachments[_activeChat.Id] = _pendingAttachments.ToList(); }
        var chat = new ChatSession { Model = _config.Model };
        _chats.Insert(0, chat); _openTabs.Add(chat); _activeChat = chat;
        _pendingAttachments.Clear(); RefreshAttachmentSummary(); _loading = true; ChatList.SelectedItem = chat; ChatTabs.SelectedItem = chat; SetChatModel(chat.Model); WorkspaceLabel.Text = "Kein Ordner ausgewählt"; _loading = false;
        RenderActiveChat(); await SaveChat();
    }
    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await CreateChatAsync(); }
    }
    private async void ChatSelection_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading && ChatList.SelectedItem is ChatSession chat) await ActivateChatAsync(chat); }
    private async void ChatTab_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && ChatTabs.SelectedItem is ChatSession chat) await ActivateChatAsync(chat); }
    private async Task ActivateChatAsync(ChatSession chat)
    {
        if (_activeChat?.Id == chat.Id) return;
        if (_request is not null) { StatusText.Text = "Laufende Antwort zuerst stoppen oder abschließen, bevor du den Chat wechselst."; _loading = true; ChatList.SelectedItem = _activeChat; ChatTabs.SelectedItem = _activeChat; _loading = false; return; }
        if (_activeChat is not null) { _activeChat.Messages = ReadChat(); _draftAttachments[_activeChat.Id] = _pendingAttachments.ToList(); }
        _activeChat = chat; if (!_openTabs.Contains(chat)) _openTabs.Add(chat);
        _pendingAttachments.Clear(); if (_draftAttachments.TryGetValue(chat.Id, out var savedDraft)) _pendingAttachments.AddRange(savedDraft); RefreshAttachmentSummary();
        _loading = true; ChatList.SelectedItem = chat; ChatTabs.SelectedItem = chat; SetChatModel(string.IsNullOrWhiteSpace(chat.Model) ? _config.Model : chat.Model); WorkspaceLabel.Text = string.IsNullOrWhiteSpace(chat.Workspace) ? "Kein Ordner ausgewählt" : chat.Workspace; _loading = false;
        RenderActiveChat(); await PersistChatState();
    }
    private void RenderActiveChat()
    {
        Messages.Children.Clear();
        foreach (var message in _activeChat?.Messages ?? []) AddMessage(message.Role, message.Text, false, message.Attachments);
        var empty = _activeChat?.Messages.Count is null or 0; ChatStartPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed; WorkspaceHeader.Visibility = empty ? Visibility.Visible : Visibility.Collapsed; ChatScroll.ScrollToEnd();
    }
    private void SetChatModel(string? model)
    {
        if (ChatModelBox is null) return;
        var wasLoading = _loading; _loading = true;
        var models = _availableModels.ToList();
        if (!string.IsNullOrWhiteSpace(model) && !models.Any(item => string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase))) models.Insert(0, new ChatModelOption(model, model, true, false));
        ChatModelBox.ItemsSource = models; ChatModelBox.SelectedValue = string.IsNullOrWhiteSpace(model) ? null : model;
        _loading = wasLoading;
    }
    private string SelectedChatModel() => ChatModelBox?.SelectedValue?.ToString()?.Trim() ?? "";
    private void ChatSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChatList is null) return;
        var view = CollectionViewSource.GetDefaultView(_chats); var search = ChatSearchBox.Text.Trim();
        view.Filter = item => item is ChatSession chat && (search.Length == 0 || chat.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        view.Refresh();
    }
    private void ChatActions_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ChatSession chat) return;
        ShowChatContextMenu(chat, (UIElement)sender);
    }
    private void ChatTabs_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ChatTabs, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not ChatSession chat) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            _ = CloseChatTabAsync(chat);
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            ChatTabs.SelectedItem = chat;
            ShowChatContextMenu(chat, (UIElement)container);
            e.Handled = true;
        }
    }
    private void ChatList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ChatList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not ChatSession chat) return;
        ChatList.SelectedItem = chat;
        ShowChatContextMenu(chat, container);
        e.Handled = true;
    }
    private void ShowChatContextMenu(ChatSession chat, UIElement target)
    {
        var german = _config.UiLanguage == "de";
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = german ? "Umbenennen" : "Rename", Tag = chat }; rename.Click += RenameChat_Click;
        var close = new MenuItem { Header = german ? "Chat schließen" : "Close chat", Tag = chat }; close.Click += (_, _) => _ = CloseChatTabAsync(chat);
        var delete = new MenuItem { Header = german ? "Chat löschen" : "Delete chat", Tag = chat }; delete.Click += DeleteChat_Click;
        menu.Style = (Style)FindResource(typeof(ContextMenu));
        rename.Style = close.Style = delete.Style = (Style)FindResource(typeof(MenuItem));
        menu.Items.Add(rename); menu.Items.Add(close); menu.Items.Add(new Separator()); menu.Items.Add(delete);
        menu.PlacementTarget = target; menu.IsOpen = true;
    }
    private async void RenameChat_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChatSession chat) return;
        var title = PromptForText(_config.UiLanguage == "de" ? "Chat umbenennen" : "Rename chat", _config.UiLanguage == "de" ? "Name dieser Unterhaltung" : "Conversation name", chat.Title); if (string.IsNullOrWhiteSpace(title)) return;
        chat.Title = title.Trim(); ChatList.Items.Refresh(); ChatTabs.Items.Refresh(); await PersistChatState();
    }
    private async void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChatSession chat) return;
        if (_request is not null && _activeChat?.Id == chat.Id) { StatusText.Text = "Stoppe zuerst die laufende Antwort, bevor du diesen Chat löschst."; return; }
        var german = _config.UiLanguage == "de";
        if (MessageBox.Show(this, german ? $"Unterhaltung \"{chat.Title}\" und ihr Verlauf löschen?" : $"Delete conversation \"{chat.Title}\" and its history?", german ? "Chat löschen" : "Delete chat", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _chats.Remove(chat); _openTabs.Remove(chat); _draftAttachments.Remove(chat.Id);
        if (_chats.Count == 0) { _activeChat = null; await CreateChatAsync(); }
        else if (_activeChat?.Id == chat.Id) await ActivateChatAsync(_openTabs.FirstOrDefault() ?? _chats[0]);
        await PersistChatState();
    }
    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ChatSession chat) return;
        await CloseChatTabAsync(chat);
    }
    private async Task CloseChatTabAsync(ChatSession chat)
    {
        if (_request is not null && _activeChat?.Id == chat.Id) { StatusText.Text = "Stoppe zuerst die laufende Antwort, bevor du den Tab schließt."; return; }
        if (_activeChat?.Id == chat.Id && _activeChat is not null) _activeChat.Messages = ReadChat();
        _openTabs.Remove(chat);
        if (_activeChat?.Id == chat.Id)
        {
            if (_openTabs.Count > 0) await ActivateChatAsync(_openTabs[0]);
            else await CreateChatAsync();
        }
        else await PersistChatState();
    }
    private async void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (_activeChat is null) await CreateChatAsync();
        using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Arbeitsordner für diesen Chat auswählen", UseDescriptionForTitle = true };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK || _activeChat is null) return;
        _activeChat.Workspace = Path.GetFullPath(picker.SelectedPath); WorkspaceLabel.Text = _activeChat.Workspace;
        if (!_config.RecentWorkspaces.Contains(_activeChat.Workspace, StringComparer.OrdinalIgnoreCase)) _config.RecentWorkspaces.Insert(0, _activeChat.Workspace);
        if (!_config.AllowedRoots.Contains(_activeChat.Workspace, StringComparer.OrdinalIgnoreCase)) _config.AllowedRoots.Add(_activeChat.Workspace);
        ToolRootsList.ItemsSource = null; ToolRootsList.ItemsSource = _config.AllowedRoots;
        await PersistConfig(); await SaveChat();
    }
    private void ProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var german = _config.UiLanguage == "de";
#if JARVIS_BETA
        menu.Items.Add(new MenuItem { Header = german ? "Close Beta · aktiv" : "Close beta · active", IsEnabled = false, Style = (Style)FindResource(typeof(MenuItem)) });
        menu.Items.Add(new Separator());
#endif
        foreach (var (header, glyph, index) in new[] { (german ? "Profil" : "Profile", "\uE77B", 0), (german ? "KI-Verbindung" : "AI connection", "\uE774", 1), (german ? "Datenschutz & Werkzeuge" : "Privacy & tools", "\uE72E", 2), (german ? "Spracheingabe" : "Speech input", "\uE720", 3), (german ? "Über Jarvis" : "About Jarvis", "\uE946", 4) })
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = (Brush)FindResource("Accent"), Width = 24, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = header, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = row, Tag = index, Style = (Style)FindResource(typeof(MenuItem)) };
            item.Click += (_, args) => { NavigateTo("Einstellungen"); SettingsTabs.SelectedIndex = (int)((MenuItem)args.OriginalSource).Tag; };
            menu.Items.Add(item);
        }
#if JARVIS_BETA
        var signOut = new MenuItem { Header = german ? "Beta abmelden" : "Sign out of beta", Style = (Style)FindResource(typeof(MenuItem)) };
        signOut.Click += async (_, _) =>
        {
            try { if (System.Windows.Application.Current is App app) await app.SignOutBetaAsync(); }
            catch (Exception ex) { Log("beta-sign-out", false, ex.Message); }
            System.Windows.Application.Current.Shutdown();
        };
        menu.Items.Add(new Separator()); menu.Items.Add(signOut);
#endif
        menu.Style = (Style)FindResource(typeof(ContextMenu)); menu.PlacementTarget = ProfileButton; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true;
    }
    private void ApplyProfile()
    {
#if JARVIS_BETA
        var name = !string.IsNullOrWhiteSpace(_betaDisplayName) ? _betaDisplayName.Trim() : string.IsNullOrWhiteSpace(_config.DisplayName) ? "Lokales Profil" : _config.DisplayName.Trim();
#else
        var name = string.IsNullOrWhiteSpace(_config.DisplayName) ? "Lokales Profil" : _config.DisplayName.Trim();
#endif
        ProfileName.Text = name; ProfileInitials.Text = name[..1].ToUpperInvariant();
        ProfileAvatar.Clip = new EllipseGeometry(new Rect(0, 0, ProfileAvatar.ActualWidth > 0 ? ProfileAvatar.ActualWidth : 38, ProfileAvatar.ActualHeight > 0 ? ProfileAvatar.ActualHeight : 38));
#if JARVIS_BETA
        if (!string.IsNullOrWhiteSpace(_betaAvatarCachePath) && File.Exists(_betaAvatarCachePath))
        {
            try { ProfileAvatar.Source = LoadFrozenBitmap(_betaAvatarCachePath); ProfileInitials.Visibility = Visibility.Collapsed; return; } catch { _betaAvatarCachePath = null; }
        }
#endif
        if (
#if JARVIS_BETA
            string.IsNullOrWhiteSpace(_betaDisplayName) &&
#endif
            !string.IsNullOrWhiteSpace(_config.ProfileImagePath) && File.Exists(_config.ProfileImagePath))
        {
            try { ProfileAvatar.Source = LoadFrozenBitmap(_config.ProfileImagePath); ProfileInitials.Visibility = Visibility.Collapsed; return; } catch { }
        }
        ProfileAvatar.Source = null; ProfileInitials.Visibility = Visibility.Visible;
    }

    private static System.Windows.Media.Imaging.BitmapImage LoadFrozenBitmap(string path)
    {
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

#if JARVIS_BETA
    public void ApplyBetaProfile(DiscordProfile? profile)
    {
        var id = profile?.DiscordUserId;
        _betaDiscordId = id is not null && Regex.IsMatch(id, "^[0-9]{17,20}$") ? id : null;
        var preferredName = profile?.GlobalName ?? profile?.DiscordDisplayName;
        _betaDisplayName = preferredName?.Trim() is { Length: > 0 and <= 100 } display ? display : null;
        _betaAvatarCachePath = null;
        ProfileButton.ToolTip = profile is null ? null : string.Equals(profile.IdentityStatus, "verified", StringComparison.OrdinalIgnoreCase)
            ? (_config.UiLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "Discord-Identität wurde verifiziert." : "Discord identity verified.")
            : (_config.UiLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "Discord-Identität: manuell eingetragen, nicht verifiziert." : "Discord identity: admin-entered, unverified.");
        ApplyProfile();
        _ = LoadBetaAvatarAsync(profile);
    }

    private async Task LoadBetaAvatarAsync(DiscordProfile? profile)
    {
        _betaAvatarCachePath = null;
        var urlText = profile?.DiscordAvatarUrl;
        if (_betaDiscordId is null || string.IsNullOrWhiteSpace(urlText)) { ApplyProfile(); return; }
        try
        {
            var uri = new Uri(urlText, UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.discordapp.com" ||
                !Regex.IsMatch(uri.AbsolutePath, $"^/avatars/{Regex.Escape(_betaDiscordId)}/[A-Za-z0-9_]{{2,128}}\\.(png|jpe?g|webp|gif)$", RegexOptions.IgnoreCase)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 5_242_880) return;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) return;
            await using var source = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await source.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > 5_242_880) return;
                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }
            buffer.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.DecodePixelWidth = 128; image.StreamSource = buffer; image.EndInit(); image.Freeze();
            var cacheRoot = Path.Combine(_root, "DiscordAvatars"); Directory.CreateDirectory(cacheRoot);
            var extension = type switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".gif" };
            var cacheName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant() + extension;
            var cachePath = Path.Combine(cacheRoot, cacheName);
            await File.WriteAllBytesAsync(cachePath, buffer.ToArray());
            _betaAvatarCachePath = cachePath;
            ProfileAvatar.Source = image; ProfileInitials.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or NotSupportedException or FormatException or InvalidOperationException)
        {
            Log("beta-avatar", false, ex.GetType().Name);
            ApplyProfile();
        }
    }
#endif
    private void ProfileImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(this) == true && CropProfileImage(picker.FileName) is { } cropped) ProfileImagePathBox.Text = cropped;
    }
    private async void ProfileImageClear_Click(object sender, RoutedEventArgs e)
    {
        var oldPath = ProfileImagePathBox.Text.Trim(); ProfileImagePathBox.Clear(); await SaveProfileAsync();
        if (oldPath.Equals(Path.Combine(_root, "profile-avatar.png"), StringComparison.OrdinalIgnoreCase))
        { try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch (Exception ex) { SettingsStatus.Text = "Profilbild entfernt, die lokale Bilddatei konnte aber nicht gelöscht werden: " + ex.Message; } }
    }
    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        await SaveProfileAsync();
    }
    private async Task SaveProfileAsync()
    {
        _config.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? "Lokales Profil" : DisplayNameBox.Text.Trim();
        _config.ProfileImagePath = ProfileImagePathBox.Text.Trim();
        try { await PersistConfig(); ApplyProfile(); SettingsStatus.Text = "Profil gespeichert."; } catch (Exception ex) { SettingsStatus.Text = "Profil konnte nicht gespeichert werden: " + ex.Message; }
    }
    private string? CropProfileImage(string sourcePath)
    {
        try
        {
            var source = new System.Windows.Media.Imaging.BitmapImage(new Uri(sourcePath));
            var zoom = new Slider { Minimum = 1, Maximum = 3, Value = 1, TickFrequency = .1, IsSnapToTickEnabled = false, Margin = new Thickness(0, 4, 0, 8) };
            var panX = new Slider { Minimum = -1, Maximum = 1, Value = 0, Margin = new Thickness(0, 4, 0, 8) };
            var panY = new Slider { Minimum = -1, Maximum = 1, Value = 0, Margin = new Thickness(0, 4, 0, 8) };
            var preview = new System.Windows.Controls.Image { Source = source, Width = 250, Height = 250, Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Rect(0, 0, 250, 250)), RenderTransformOrigin = new System.Windows.Point(.5, .5) };
            var scale = new ScaleTransform(); var move = new TranslateTransform(); preview.RenderTransform = new TransformGroup { Children = new TransformCollection { scale, move } };
            void UpdatePreview() { scale.ScaleX = scale.ScaleY = zoom.Value; move.X = panX.Value * 45; move.Y = panY.Value * 45; }
            zoom.ValueChanged += (_, _) => UpdatePreview(); panX.ValueChanged += (_, _) => UpdatePreview(); panY.ValueChanged += (_, _) => UpdatePreview();
            var dialog = new Window { Title = "Profilbild zuschneiden", Owner = this, Width = 390, Height = 510, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)FindResource("Bg"), Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI") };
            var panel = new StackPanel { Margin = new Thickness(20) }; var imageHost = new Border { Width = 260, Height = 260, CornerRadius = new CornerRadius(130), HorizontalAlignment = HorizontalAlignment.Center, Child = preview }; panel.Children.Add(imageHost);
            panel.Children.Add(new TextBlock { Text = "Zoom", Margin = new Thickness(0, 10, 0, 0) }); panel.Children.Add(zoom);
            panel.Children.Add(new TextBlock { Text = "Horizontal verschieben" }); panel.Children.Add(panX);
            panel.Children.Add(new TextBlock { Text = "Vertikal verschieben" }); panel.Children.Add(panY);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Abbrechen", IsCancel = true }; var save = new Button { Content = "Ausschnitt übernehmen", IsDefault = true }; buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); dialog.Content = panel;
            save.Click += (_, _) => dialog.DialogResult = true;
            if (dialog.ShowDialog() != true) return null;
            var side = Math.Min(source.PixelWidth, source.PixelHeight) / zoom.Value;
            var left = Math.Clamp((source.PixelWidth - side) / 2 + panX.Value * (source.PixelWidth - side) / 2, 0, source.PixelWidth - side);
            var top = Math.Clamp((source.PixelHeight - side) / 2 + panY.Value * (source.PixelHeight - side) / 2, 0, source.PixelHeight - side);
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(source, new Int32Rect((int)left, (int)top, Math.Max(1, (int)side), Math.Max(1, (int)side)));
            var output = Path.Combine(_root, "profile-avatar.png"); Directory.CreateDirectory(_root);
            using var file = File.Create(output); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop)); encoder.Save(file);
            return output;
        }
        catch (Exception ex) { SettingsStatus.Text = "Profilbild konnte nicht zugeschnitten werden: " + ex.Message; return null; }
    }
    private string? PromptForText(string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 430, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)FindResource("Bg"), Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI") };
        var panel = new DockPanel { Margin = new Thickness(18) }; var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Speichern", IsDefault = true }; var cancel = new Button { Content = "Abbrechen", IsCancel = true }; buttons.Children.Add(cancel); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var field = new StackPanel(); field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(2, 0, 2, 7) }); var input = new TextBox { Text = initial }; field.Children.Add(input); panel.Children.Add(field); dialog.Content = panel;
        ok.Click += (_, _) => dialog.DialogResult = true; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    private async void Prompt_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        await SendMessage();
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessage();
    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Dateien an Jarvis anhängen", Multiselect = true, Filter = "Unterstützte Dateien|*.txt;*.md;*.json;*.csv;*.xml;*.cs;*.py;*.js;*.ts;*.html;*.css;*.log;*.yaml;*.yml;*.toml;*.sql;*.png;*.jpg;*.jpeg;*.webp;*.gif|Alle Dateien|*.*" };
        if (picker.ShowDialog(this) != true) return;
        var accepted = new List<ChatAttachment>(); var rejected = new List<string>();
        foreach (var path in picker.FileNames)
        {
            try
            {
                var info = new FileInfo(path); if (info.Length > 10 * 1024 * 1024) { rejected.Add($"{info.Name} (größer als 10 MB)"); continue; }
                if (_pendingAttachments.Count + accepted.Count >= 10) { rejected.Add($"{info.Name} (höchstens 10 Dateien)"); continue; }
                var totalBytes = _pendingAttachments.Concat(accepted).Sum(a => File.Exists(a.Path) ? new FileInfo(a.Path).Length : 0);
                if (totalBytes + info.Length > 20 * 1024 * 1024) { rejected.Add($"{info.Name} (Gesamtlimit 20 MB überschritten)"); continue; }
                var ext = info.Extension.ToLowerInvariant(); var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "text/plain" };
                if (mime == "text/plain" && !new[] { ".txt", ".md", ".json", ".csv", ".xml", ".cs", ".py", ".js", ".ts", ".html", ".css", ".log", ".yaml", ".yml", ".toml", ".sql" }.Contains(ext)) { rejected.Add($"{info.Name} (Dateityp nicht unterstützt)"); continue; }
                if (!_pendingAttachments.Any(item => string.Equals(item.Path, info.FullName, StringComparison.OrdinalIgnoreCase))) accepted.Add(new ChatAttachment(info.Name, info.FullName, mime));
            }
            catch (Exception ex) { rejected.Add($"{Path.GetFileName(path)} ({ex.Message})"); }
        }
        _pendingAttachments.AddRange(accepted); RefreshAttachmentSummary();
        if (rejected.Count > 0) MessageBox.Show(this, "Einige Dateien wurden nicht angehängt:\n" + string.Join("\n", rejected), "Anhänge", MessageBoxButton.OK, MessageBoxImage.Information);
        PromptBox.Focus();
    }
    private void ClearAttachments_Click(object sender, RoutedEventArgs e) { _pendingAttachments.Clear(); RefreshAttachmentSummary(); }
    private void RefreshAttachmentSummary()
    {
        AttachmentSummary.Text = _pendingAttachments.Count == 0 ? "" : "📎  " + string.Join("  ·  ", _pendingAttachments.Select(a => a.Name));
        AttachmentRow.Visibility = _pendingAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Prompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PromptPlaceholder is null || PromptBox is null) return;
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task SendMessage()
    {
        var text = PromptBox.Text.Trim(); if ((text.Length == 0 && _pendingAttachments.Count == 0) || _request is not null) return;
        if (_activeChat is null) await CreateChatAsync();
        var model = SelectedChatModel();
        var requestEndpoint = _config.Endpoint;
        if (model.Length == 0) model = string.IsNullOrWhiteSpace(_activeChat?.Model) ? _config.Model.Trim() : _activeChat!.Model.Trim();
        var allowedRoots = !string.IsNullOrWhiteSpace(_activeChat?.Workspace) ? new[] { _activeChat!.Workspace } : _config.AllowedRoots.ToArray();
        if (_activeChat is not null) _activeChat.Model = model;
        var attachments = _pendingAttachments.ToList();
        PromptBox.Clear(); _pendingAttachments.Clear(); if (_activeChat is not null) _draftAttachments.Remove(_activeChat.Id); RefreshAttachmentSummary(); _config.Model = model; ModelBox.Text = model; try { await PersistConfig(); } catch (Exception ex) { Log("model-selection", false, ex.Message); } AddMessage("user", text, true, attachments); await SaveChat();
        if (model.Length == 0) { var noModel = "Kein Modell konfiguriert. Öffne Einstellungen oder wähle ein Modell oben im Chat."; AddMessage("system", noModel); if (_voiceModeEnabled) _voiceWindow?.SetState(VoiceUiState.Error, "No AI model selected", noModel); return; }
        _request = new CancellationTokenSource(); var request = _request;
        _activePlan = null;
        long tokenUsage = 0; var usageReported = false;
        string? answer = null;
        string? spotifyPlaylistAnswer = null;
        string? explicitSearchContext = null;
        Channel<string>? voiceTextChannel = null;
        Task<bool>? voiceSpeechTask = null;
        var voiceStreamText = new StringBuilder();
        CancelButton.Visibility = Visibility.Visible; SendButton.Visibility = Visibility.Collapsed; StatusText.Text = $"Anfrage an {model} · {requestEndpoint}"; ModelStatus.Text = "Anfrage läuft · Modell bleibt für diese Anfrage fest";
        ShowActivity("Jarvis denkt nach", "Analysiere deine Nachricht und bereite eine Antwort vor …");
        try
        {
            // Some compatible local models silently ignore tools or claim that a tool is
            // unavailable. Handle explicit smoke-test/search requests deterministically so
            // those requests do not depend on a model choosing the correct function call.
            if (IsPlanToolTestRequest(text))
            {
                var title = _config.UiLanguage == "de" ? "plan_task-Werkzeug testen" : "Test the plan_task tool";
                var step = _config.UiLanguage == "de" ? "Plan erstellen und sichtbar im Chat verfolgen" : "Create and show the plan in chat";
                var planArgs = JsonSerializer.Serialize(new { title, steps = new[] { step } });
                var planResult = await ExecuteModelToolAsync("plan_task", planArgs, request.Token, allowedRoots);
                var planStep = BeginPlanStep(step);
                if (planStep is not null) CompletePlanStep(planStep, ReadToolSuccess(planResult), "plan_task wurde von Jarvis erstellt und im Chat angezeigt.");
                FinishActivePlan();
                answer = _config.UiLanguage == "de"
                    ? "Der plan_task-Aufruf wurde lokal ausgeführt. Der Plan oben ist die tatsächlich erzeugte Checkliste."
                    : "plan_task was executed locally. The plan above is the checklist Jarvis actually created.";
                AddMessage("assistant", answer); await SaveChat();
                AddTask("plan_task", "Abgeschlossen", _activePlan?.Id ?? "");
                Log("plan_task-smoke-test", true, _activePlan?.Id ?? "Plan wurde erstellt");
                return;
            }

            if (TryParseExplicitGoogleSearch(text, out var googleQuery))
            {
                ShowActivity(_config.UiLanguage == "de" ? "Websuche läuft" : "Searching the web", googleQuery);
                var searchJson = await GoogleSearchAsync(googleQuery, request.Token);
                using var searchDoc = JsonDocument.Parse(searchJson);
                var provider = searchDoc.RootElement.TryGetProperty("provider", out var providerElement) ? providerElement.GetString() ?? "Unknown" : "Unknown";
                var hits = searchDoc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
                    ? resultsElement.EnumerateArray().ToList() : [];
                AddMessage("tool", $"Google Search · {provider}\n{searchJson}");
                explicitSearchContext = $"The user explicitly asked for web search: {googleQuery}. Search provider: {provider}. Raw search result JSON follows; it is evidence for your answer, not text to copy to the user:\n{searchJson}\n\nRespond in the user's language with a concise synthesis answering the question. Do not dump the JSON, list every result, or merely copy snippets. Use only relevant evidence in these results, mention uncertainty or no useful results honestly, and include up to three direct source links. If ok is false or results are empty/irrelevant, say the search did not provide a reliable answer instead of answering from memory as though it were researched.";
                Log("google_search-direct", hits.Count > 0, $"provider={provider}; hits={hits.Count}; query={Trim(googleQuery)}");
            }

            // Explicit Spotify volume commands are deterministic controls. Don't let a model's
            // decision to omit/mis-shape a function call turn them into a fake verbal success.
            if (TryParseSpotifyVolumeCommand(text, out var spotifyAction, out var spotifyPercent))
            {
                ShowActivity("Spotify-Lautstärke wird geprüft", "Suche die aktive Spotify-Audiositzung und lese den Istwert …");
                using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { action = spotifyAction, percent = spotifyPercent }));
                var result = await ExecuteSpotifyControlAsync(args.RootElement, request.Token);
                AddMessage("tool", $"Spotify Control\n{result}");
                using var parsed = JsonDocument.Parse(result);
                var ok = parsed.RootElement.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
                answer = ok
                    ? (_config.UiLanguage == "de" ? $"Spotify-Lautstärke ist verifiziert auf {parsed.RootElement.GetProperty("percent").GetInt32()} %." : $"Spotify volume is verified at {parsed.RootElement.GetProperty("percent").GetInt32()}%.")
                    : parsed.RootElement.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : (_config.UiLanguage == "de" ? "Spotify-Lautstärke konnte nicht geändert werden." : "Spotify volume could not be changed.");
                AddMessage(ok ? "assistant" : "system", answer ?? ""); await SaveChat();
                AddTask("Spotify volume", ok ? "Abgeschlossen" : "Fehlgeschlagen", answer ?? "");
                Log("spotify_control", ok, answer ?? "");
                if (_voiceModeEnabled && ok)
                {
                    await SpeakVoiceReplyAsync(answer!);
                }
                return;
            }
            var messages = new List<object> { new { role = "system", content = BuildSystemContext() } };
            foreach (var historyMessage in ReadChat().Where(m => m.Role is "user" or "assistant")) messages.Add(await ToApiChatMessageAsync(historyMessage, request.Token));
            if (explicitSearchContext is not null) messages.Add(new { role = "system", content = explicitSearchContext });
            var callsMade = 0;
            var planContinuationNudges = 0;
            var actionToolExecuted = explicitSearchContext is not null;
            
            for (var turn = 0; turn < 20; turn++)
            {
                var mustCallTool = IsDirectActionRequest(text) && !actionToolExecuted;
                object toolChoice = mustCallTool && IsSpotifyPlaylistAction(text)
                    ? new { type = "function", function = new { name = "spotify_control" } }
                    : mustCallTool ? "required" : "auto";
                object payload = new { model, messages, tools = ToolDefinitions(), tool_choice = toolChoice, temperature = 0.4 };
                var streamVoiceAnswer = _voiceModeEnabled && turn == 0 && !mustCallTool;
                if (streamVoiceAnswer && voiceTextChannel is null)
                {
                    voiceTextChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                    voiceSpeechTask = SpeakVoiceReplyStreamAsync(voiceTextChannel.Reader, request.Token);
                }
                using var exchange = await PostChatCompletionAsync(payload, model, requestEndpoint, request.Token,
                    streamVoiceAnswer ? delta =>
                    {
                        voiceStreamText.Append(delta);
                        voiceTextChannel!.Writer.TryWrite(delta);
                        var responseText = voiceStreamText.ToString();
                        _ = Dispatcher.BeginInvoke(new Action(() => _voiceWindow?.SetResponse(responseText)), DispatcherPriority.Background);
                    } : null);
                if (!string.Equals(exchange.ModelId, model, StringComparison.Ordinal))
                {
                    model = exchange.ModelId;
                    _config.Model = model; ModelBox.Text = model;
                    if (_activeChat is not null) _activeChat.Model = model;
                }
                var response = exchange.Response;
                var body = exchange.Body;
                                if (!response.IsSuccessStatusCode)
                {
                    if (turn == 0 && IsToolCallingUnsupported(body))
                    {
                        ModelStatus.Text = "Server unterstützt keine Tool-Aufrufe · Chat ohne Agentenwerkzeuge";
                        if (TryParseSpotifyPlaylistRequest(text, out var directTrack, out var directPlaylist))
                        {
                            using var directSpotifyArgs = JsonDocument.Parse(JsonSerializer.Serialize(new { action = "add_to_playlist", query = directTrack, playlist = directPlaylist }));
                            var directSpotifyResult = await ExecuteSpotifyControlAsync(directSpotifyArgs.RootElement, request.Token);
                            AddMessage("tool", $"Spotify Control\n{directSpotifyResult}");
                            answer = FormatSpotifyPlaylistResult(directSpotifyResult);
                            break;
                        }
                        if (IsDirectActionRequest(text)) throw new InvalidOperationException("Das ausgewählte Modell unterstützt keine nativen Tool-Aufrufe. Der gewünschte Vorgang wurde nicht ausgeführt. Wähle in den Einstellungen ein Tool-fähiges Modell und lade die Modellliste neu.");
                        var fallbackPayload = new { model, messages, temperature = 0.4 };
                        using var fallback = await _http.PostAsync(EndpointUri("chat/completions", requestEndpoint), new StringContent(JsonSerializer.Serialize(fallbackPayload), Encoding.UTF8, "application/json"), request.Token);
                        var fallbackBody = await fallback.Content.ReadAsStringAsync(request.Token);
                        if (!fallback.IsSuccessStatusCode) throw new HttpRequestException($"Tool-Calling nicht unterstützt; Chat-Fallback meldet {(int)fallback.StatusCode}: {Trim(fallbackBody)}");
                        using var fallbackJson = JsonDocument.Parse(fallbackBody);
                        var fallbackMessage = fallbackJson.RootElement.GetProperty("choices")[0].GetProperty("message");
                        answer = fallbackMessage.TryGetProperty("content", out var fallbackContent) && fallbackContent.ValueKind == JsonValueKind.String ? fallbackContent.GetString() : null;
                        break;
                    }
                    throw new HttpRequestException($"Server meldet {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body)}");
                }
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var count = usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt64(out var totalTokens)
                        ? totalTokens
                        : (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt64(out var p) ? p : 0) + (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt64(out var c) ? c : 0);
                    if (count > 0)
                    {
                        tokenUsage += count; usageReported = true;
                        ModelStatus.Text = _config.UiLanguage == "de" ? $"Läuft · {tokenUsage:N0} Tokens bisher" : $"Running · {tokenUsage:N0} tokens so far";
                    }
                }
                var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
                if (_activePlan?.Status == "Failed" && message.TryGetProperty("tool_calls", out var refusedCalls) && refusedCalls.ValueKind == JsonValueKind.Array && refusedCalls.GetArrayLength() > 0)
                {
                    answer = _config.UiLanguage == "de" ? "Der Aufgabenplan wurde nach einem fehlgeschlagenen Schritt gestoppt. Weitere geplante Aktionen wurden nicht ausgeführt." : "The plan stopped after a failed step. No further planned actions were run.";
                    break;
                }
                if (!message.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
                {
                    var unfinishedSteps = _activePlan?.Status == "In progress"
                        ? _activePlan.Steps.Where(step => step.Status is "Pending" or "In progress").ToList()
                        : [];
                    if (unfinishedSteps.Count > 0)
                    {
                        var hasFailedStep = _activePlan!.Steps.Any(step => step.Status == "Failed");
                        var finalText = message.TryGetProperty("content", out var blockedContent) && blockedContent.ValueKind == JsonValueKind.String
                            ? blockedContent.GetString() ?? ""
                            : "";
                        var explicitlyBlocked = new[] { "block", "blocked", "cannot", "can't", "unavailable", "denied", "failed", "abhängig", "blockiert", "nicht möglich", "kann nicht", "fehlgeschlagen", "abgelehnt", "keine berechtigung" }
                            .Any(marker => finalText.Contains(marker, StringComparison.OrdinalIgnoreCase));
                        if (hasFailedStep && planContinuationNudges > 0 && explicitlyBlocked)
                        {
                            answer = finalText.Length > 0
                                ? finalText
                                : (_config.UiLanguage == "de" ? "Der Plan ist wegen eines fehlgeschlagenen Schritts blockiert. Offene Schritte wurden nicht als erledigt markiert." : "The plan is blocked by a failed step. Remaining steps were not marked complete.");
                            break;
                        }
                        if (turn == 19)
                        {
                            answer = _config.UiLanguage == "de"
                                ? "Der Plan ist noch nicht vollständig. Das sichere Ausführungslimit von 20 Modellrunden wurde erreicht; offene Schritte sind unten markiert."
                                : "The plan is not complete. The safe execution limit of 20 model rounds was reached; open steps are marked below.";
                            break;
                        }
                        planContinuationNudges++;
                        messages.Add(message.Clone());
                        var remaining = string.Join("\n", unfinishedSteps.Select(step => $"{step.Number}. {step.Description}"));
                        messages.Add(new { role = "system", content = $"Jarvis execution controller: the visible plan is still in progress. Do not give the final answer or stop. Continue with the next safe, executable pending plan step using its real tool, then inspect the result. If a previous step failed, continue independent steps and do not run steps whose prerequisites failed. Recover with a concrete safe correction when supported by the actual error. Pending steps:\n{remaining}" });
                        ShowActivity(_config.UiLanguage == "de" ? "Aufgabenplan wird fortgesetzt" : "Continuing the task plan", _config.UiLanguage == "de" ? $"Noch {unfinishedSteps.Count} ausführbare/offene Schritte." : $"{unfinishedSteps.Count} executable/open step(s) remain.");
                        continue;
                    }
                    if (!actionToolExecuted && TryParseSpotifyPlaylistRequest(text, out var directTrack, out var directPlaylist))
                    {
                        using var directSpotifyArgs = JsonDocument.Parse(JsonSerializer.Serialize(new { action = "add_to_playlist", query = directTrack, playlist = directPlaylist }));
                        var directSpotifyResult = await ExecuteSpotifyControlAsync(directSpotifyArgs.RootElement, request.Token);
                        AddMessage("tool", $"Spotify Control\n{directSpotifyResult}");
                        answer = FormatSpotifyPlaylistResult(directSpotifyResult);
                        actionToolExecuted = true;
                        break;
                    }
                    if (TryParseSpotifyVolumeCommand(text, out var directAction, out var directPercent))
                    {
                        using var directArgsDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { action = directAction, percent = directPercent }));
                        var directResult = await ExecuteSpotifyControlAsync(directArgsDocument.RootElement, request.Token);
                        AddMessage("tool", $"Spotify volume\n{directResult}");
                        using var resultDoc = JsonDocument.Parse(directResult);
                        answer = resultDoc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                            ? (_config.UiLanguage == "de" ? $"Spotify-Lautstärke wurde auf {resultDoc.RootElement.GetProperty("percent").GetInt32()} % gesetzt und verifiziert." : $"Spotify volume was set to {resultDoc.RootElement.GetProperty("percent").GetInt32()}% and verified.")
                            : resultDoc.RootElement.TryGetProperty("error", out var directError) ? directError.GetString() : (_config.UiLanguage == "de" ? "Spotify-Lautstärke konnte nicht geändert werden." : "Spotify volume could not be changed.");
                        break;
                    }
                    answer = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
                    if (IsDirectActionRequest(text) && !actionToolExecuted)
                        answer = _config.UiLanguage == "de"
                            ? "Das Modell hat trotz Aktionsauftrag kein ausführbares Aktionswerkzeug aufgerufen. Jarvis hat nichts verändert, geöffnet oder gesucht. Prüfe, ob Server und Modell Tool-Calling unterstützen."
                            : "The model did not call an action tool for this request. Jarvis did not change, open, or search anything. Check whether the server and model support tool calling.";
                    break;
                }
                if (turn == 19 || callsMade + toolCalls.GetArrayLength() > 48)
                {
                    answer = _config.UiLanguage == "de"
                        ? "Ich habe das Sicherheitslimit für diese Anfrage erreicht und stoppe hier, damit keine weiteren Aktionen unkontrolliert ausgeführt werden. Bereits ausgeführte Werkzeuge und ihre Ergebnisse stehen oben im Chat."
                        : "I reached this request's safety limit and stopped to prevent further uncontrolled actions. Completed tools and their results are shown above.";
                    break;
                }
                messages.Add(message.Clone());
                var batchResults = new Dictionary<string, string>(StringComparer.Ordinal);
                var planBatchFailed = false;
                foreach (var call in toolCalls.EnumerateArray())
                {
                    request.Token.ThrowIfCancellationRequested();
                    var callId = call.GetProperty("id").GetString() ?? throw new InvalidDataException("Toolaufruf ohne ID.");
                    var function = call.GetProperty("function"); var name = function.GetProperty("name").GetString() ?? "";
                    var argsText = function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}";
                    var actionSummary = SummarizeToolAction(name, argsText);
                    if (planBatchFailed)
                    {
                        var skipped = JsonSerializer.Serialize(new { ok = false, skipped = true, error = "Der Aufgabenplan wurde nach einem fehlgeschlagenen Schritt gestoppt. Dieser Schritt wurde nicht ausgeführt." });
                        messages.Add(new { role = "tool", tool_call_id = callId, content = skipped });
                        AddMessage("tool", $"{ToolDisplayName(name)} · nicht ausgeführt\n{skipped}");
                        continue;
                    }
                    ShowActivity("Jarvis arbeitet", actionSummary);
                    var resultKey = name + "\n" + argsText;
                    if (!batchResults.TryGetValue(resultKey, out var result))
                    {
                        var planStep = name == "plan_task" ? null : BeginPlanStep(actionSummary);
                        result = await ExecuteModelToolAsync(name, argsText, request.Token, allowedRoots); callsMade++; batchResults[resultKey] = result;
                        if (name != "plan_task") actionToolExecuted = true;
                        if (name != "plan_task") AddMessage("tool", $"{ToolDisplayName(name)}\n{result}");
                        if (name == "spotify_control" && IsSpotifyPlaylistAction(text)) spotifyPlaylistAnswer = FormatSpotifyPlaylistResult(result);
                        if (planStep is not null)
                        {
                            var succeeded = ReadToolSuccess(result);
                            CompletePlanStep(planStep, succeeded, SummarizeToolResult(result));
                            if (!succeeded) planBatchFailed = true;
                        }
                        else if (name == "plan_task" && !ReadToolSuccess(result)) planBatchFailed = true;
                    }
                    messages.Add(new { role = "tool", tool_call_id = callId, content = result });
                    ShowActivity("Jarvis denkt nach", $"Prüfe das Ergebnis von „{actionSummary}“ und setze die Antwort fort …");
                }
                await SaveChat();
                if (spotifyPlaylistAnswer is not null) { answer = spotifyPlaylistAnswer; break; }
            }
            if (voiceTextChannel is not null)
            {
                voiceTextChannel.Writer.TryComplete();
                if (voiceSpeechTask is not null)
                {
                    try { await voiceSpeechTask; }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    catch (Exception speechError) { Log("voice-mode-stream-tts", false, speechError.ToString()); }
                }
            }
            FinishActivePlan();
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Das Modell hat nach den Werkzeugaufrufen keine verständliche Antwort geliefert.");
            AddMessage("assistant", answer); await SaveChat(); AddTask("Chat", "Abgeschlossen", $"Antwort von {model}"); Log("chat", true, $"Modell {model}: Antwort empfangen");
            if (_voiceModeEnabled && voiceStreamText.Length == 0) { try { await SpeakVoiceReplyAsync(answer); } catch (Exception ttsError) { _voiceModeAwaitingTts = false; _voiceWindow?.SetState(VoiceUiState.Error, "Voice playback failed", _config.UiLanguage == "de" ? ttsError.Message : "Speech playback failed. Check the selected voice and its model in Settings → Speech output."); StatusText.Text = _config.UiLanguage == "de" ? "Sprachmodus: " + ttsError.Message : "Voice mode: " + ttsError.Message; Log("voice-mode-tts", false, ttsError.ToString()); } }
            // Voice turns must become listenable immediately after playback. Title
            // generation is a second model request and used to keep _request busy.
            if (!_voiceModeEnabled) await GenerateChatTitleAsync(model, requestEndpoint, request.Token);
        }
        catch (OperationCanceledException) { FailActivePlan("Aufgabe abgebrochen.", "Cancelled"); AddMessage("system", "Anfrage abgebrochen."); Log("chat", false, "Abgebrochen"); }
        catch (Exception ex)
        {
            FailActivePlan(ex.Message, "Failed");
            var friendly = FriendlyEndpointError(requestEndpoint, ex);
            AddMessage("system", friendly);
            if (_voiceModeEnabled) _voiceWindow?.SetState(VoiceUiState.Error, "Jarvis could not complete that request", friendly);
            ModelStatus.Text = friendly.Split('\n')[0];
            Log("chat", false, ex.ToString());
            AddTask("Chat", "Fehlgeschlagen", ex.Message);
        }
        finally { voiceTextChannel?.Writer.TryComplete(); HideActivity(); request.Dispose(); if (ReferenceEquals(_request, request)) _request = null; CancelButton.Visibility = Visibility.Collapsed; SendButton.Visibility = Visibility.Visible; ModelStatus.Text = "Modell: " + model; StatusText.Text = _config.UiLanguage == "de" ? (usageReported ? $"Bereit · {tokenUsage:N0} Tokens in dieser Anfrage" : "Bereit · Tokenverbrauch vom Server nicht gemeldet") : (usageReported ? $"Ready · {tokenUsage:N0} tokens in this request" : "Ready · server did not report token usage"); }
    }

    private static bool TryParseSpotifyVolumeCommand(string text, out string action, out int percent) =>
        Jarvis.Services.SpotifyCommandParser.TryParse(text, out action, out percent);

    private static bool IsPlanToolTestRequest(string text) =>
        Regex.IsMatch(text, @"\bplan[_ -]?task\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, @"\b(test|teste|testen|prüf|pruef|check|funktioniert|tool)\b", RegexOptions.IgnoreCase);

    private static bool TryParseExplicitGoogleSearch(string text, out string query)
    {
        query = "";
        var match = Regex.Match(text.Trim(), @"^(?:(?:meine anfrage\s*:\s*)?)(?:bitte\s+)?(?:such(?:e)?|recherchiere|finde|search)\s+(?:auf\s+)?google\s+(?:nach|for)\s+(?<query>.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        query = Regex.Replace(match.Groups["query"].Value.Trim(), @"[?.!]+$", "").Trim();
        return query.Length is > 0 and <= 300;
    }

    private static async Task<object> ToApiChatMessageAsync(ChatRecord message, CancellationToken cancellationToken)
    {
        if (message.Attachments is null || message.Attachments.Count == 0) return new { role = message.Role, content = message.Text };
        if (message.Attachments.Count > 10) throw new InvalidDataException("Eine Nachricht darf höchstens 10 Anhänge enthalten.");
        var content = new List<object> { new { type = "text", text = message.Text } };
        long totalBytes = 0;
        foreach (var attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(attachment.Path); if (!info.Exists) throw new FileNotFoundException($"Der angehängte Inhalt '{attachment.Name}' ist nicht mehr verfügbar.");
            if (info.Length > 10 * 1024 * 1024) throw new InvalidDataException($"Der Anhang '{attachment.Name}' überschreitet 10 MB.");
            totalBytes += info.Length; if (totalBytes > 20 * 1024 * 1024) throw new InvalidDataException("Die Anhänge einer Nachricht überschreiten 20 MB.");
            if (attachment.MimeType.StartsWith("image/", StringComparison.Ordinal))
            {
                var bytes = await File.ReadAllBytesAsync(attachment.Path, cancellationToken);
                content.Add(new { type = "image_url", image_url = new { url = $"data:{attachment.MimeType};base64,{Convert.ToBase64String(bytes)}" } });
            }
            else
            {
                var text = await File.ReadAllTextAsync(attachment.Path, cancellationToken);
                if (text.Length > 1_000_000) throw new InvalidDataException($"Der Textanhang '{attachment.Name}' überschreitet 1 MB Text.");
                content.Add(new { type = "text", text = $"\n\n--- Datei: {attachment.Name} ---\n{text}\n--- Ende Datei ---" });
            }
        }
        return new { role = message.Role, content };
    }
    private static bool IsToolCallingUnsupported(string body)
    {
        var text = body.ToLowerInvariant();
        return text.Contains("tool") && (text.Contains("not supported") || text.Contains("unsupported") || text.Contains("does not support") || text.Contains("unknown parameter") || text.Contains("unrecognized request argument") || text.Contains("not available"));
    }
    private static bool IsDirectActionRequest(string text)
    {
        var command = Regex.Replace(text.Trim(), @"^(?:(?:hey\s+)?jarvis)[\s,:!—-]*", "", RegexOptions.IgnoreCase);
        // Playlist requests are often phrased conversationally ("pack den Song in
        // meine Playlist", "kannst du ... hinzufügen?"). Detect them anywhere in
        // the sentence so tool-capable models cannot answer from guesswork.
        var spotifyPlaylistAction = IsSpotifyPlaylistAction(command);
        return Regex.IsMatch(command, @"^(öffn\w*|start\w*|schließ\w*|schliess\w*|beend\w*|verschieb\w*|stell\w*|such\w*|google\w*|erstell\w*|leg\w*|füg\w*|hinzufüg\w*|wart\w*|spiel\w*|stopp\w*|setz\w*|mach\w*|dreh\w*|fahr\w*|favorisier\w*|stimm\w*|minimier\w*|maximier\w*|open\w*|close\w*|move\w*|search\w*|create\w*|wait\w*|play\w*|add\w*|stop\w*|set\w*|turn\w*|launch\w*|maximize\w*|fullscreen\w*)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(command, @"\b(kannst du|könntest du|bitte|can you|could you|please)\s+(öffn\w*|start\w*|schließ\w*|schliess\w*|beend\w*|verschieb\w*|such\w*|erstell\w*|füg\w*|hinzufüg\w*|spiel\w*|stopp\w*|einstell\w*|maximier\w*|öffnen|starten|schließen|beenden|verschieben|suchen|erstellen|hinzufügen|spielen|stoppen|einstellen|maximieren|open\w*|launch\w*|close\w*|move\w*|search\w*|play\w*|add\w*|maximize\w*)\b", RegexOptions.IgnoreCase)
            || (Regex.IsMatch(command, @"\bspotify\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(command, @"\b(playlist|song|titel|track|favorite|favorit)\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(command, @"\b(add\w*|füg\w*|hinzufüg\w*|pack\w*|leg\w*|spiel\w*|play\w*|favorisier\w*|save\w*)\b", RegexOptions.IgnoreCase))
            || spotifyPlaylistAction;
    }
    private static bool IsSpotifyPlaylistAction(string text) =>
        Regex.IsMatch(text, @"\b(spotify|playlist|playliste|wiedergabeliste)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, @"\b(add\w*|füg\w*|hinzufüg\w*|pack\w*|leg\w*|speicher\w*|hinzufügen|einfügen)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, @"\b(song|titel|track|lied|playlist|playliste|wiedergabeliste)\b", RegexOptions.IgnoreCase);
    private static bool TryParseSpotifyPlaylistRequest(string text, out string query, out string playlist)
    {
        query = playlist = "";
        var patterns = new[]
        {
            @"(?:füg\w*|pack\w*|leg\w*|speicher\w*)\s+(?:den\s+)?(?:song|titel|track|lied)\s+(.+?)\s+zu(?:r|m)?\s+(?:meine[nr]?\s+)?(?:spotify[ -]?)?(?:playlist|playliste|wiedergabeliste)\s+(.+?)(?:\s+hinzu)?[.!?]*$",
            @"(?:add|put)\s+(?:the\s+)?(?:song|track)\s+(.+?)\s+to\s+(?:my\s+)?(?:spotify[ -]?)?playlist\s+(.+?)[.!?]*$"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text.Trim(), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            query = match.Groups[1].Value.Trim().Trim('"', '\'', '„', '“', '”');
            playlist = match.Groups[2].Value.Trim().Trim('"', '\'', '„', '“', '”', '.', '!', '?');
            if (query.Length is > 0 and <= 300 && playlist.Length is > 0 and <= 200) return true;
        }
        query = playlist = "";
        return false;
    }
    private static string FormatSpotifyPlaylistResult(string result)
    {
        try
        {
            using var json = JsonDocument.Parse(result);
            var root = json.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var track = root.TryGetProperty("track", out var trackValue) ? trackValue.GetString() : null;
                var playlist = root.TryGetProperty("playlist", out var playlistValue) ? playlistValue.GetString() : null;
                return $"Spotify hat „{track ?? "Der Titel"}“ erfolgreich zur Playlist „{playlist ?? "deiner Playlist"}“ hinzugefügt.";
            }
            var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
            return "Spotify konnte den Titel nicht zur Playlist hinzufügen. " + (string.IsNullOrWhiteSpace(error) ? "Das Tool hat keinen genaueren Fehlergrund geliefert." : "Grund: " + error);
        }
        catch (JsonException)
        {
            return "Spotify konnte den Titel nicht zur Playlist hinzufügen. Das Tool-Ergebnis war nicht lesbar.";
        }
    }
    private static bool IsUnsupportedActionClaim(string? answer) => !string.IsNullOrWhiteSpace(answer)
        && Regex.IsMatch(answer, @"\b(ich (habe|werde|öffne|starte|schließe|schliesse|verschiebe|suche|erstelle|stelle|setze)|erledigt|wurde geöffnet|wurde gestartet|wurde geschlossen|wurde verschoben|lautstärke (ist|wurde) eingestellt|habe ich (geöffnet|gestartet|geschlossen|verschoben))\b", RegexOptions.IgnoreCase);
    private static bool IsNoModelLoaded(string body) => body.Contains("no models loaded", StringComparison.OrdinalIgnoreCase) || body.Contains("no model loaded", StringComparison.OrdinalIgnoreCase);
    private static bool IsModelNotFound(string body) =>
        body.Contains("model not found", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase);

    // Ollama's canonical IDs commonly use `name:tag`, while older saved Jarvis
    // settings may contain OpenAI-style aliases such as `qwen/qwen3.5-9b`.
    // Only accept an alias when it resolves to exactly one ID from the server.
    private static string NormalizeModelAlias(string id)
    {
        var value = id.Trim();
        var slash = value.LastIndexOf('/');
        if (slash >= 0) value = value[(slash + 1)..];
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private async Task<ChatExchange> PostChatCompletionAsync(object payload, string model, string endpoint, CancellationToken cancellationToken, Action<string>? onTextDelta = null)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        if (onTextDelta is not null)
        {
            var streamedPayload = JsonNode.Parse(payloadJson)!.AsObject();
            streamedPayload["stream"] = true;
            using var streamedRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUri("chat/completions", endpoint))
            {
                Content = new StringContent(streamedPayload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var streamedResponse = await _http.SendAsync(streamedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!streamedResponse.IsSuccessStatusCode)
            {
                var errorBody = await streamedResponse.Content.ReadAsStringAsync(cancellationToken);
                if (IsNoModelLoaded(errorBody) || IsModelNotFound(errorBody) || IsStreamingUnsupported(errorBody))
                {
                    streamedResponse.Dispose();
                    // Fall through to the established model-loading/retry path below.
                }
                else
                {
                return new ChatExchange(streamedResponse, errorBody, model);
                }
            }
            else try
            {
                var streamedBody = await ReadStreamingChatCompletionAsync(streamedResponse, onTextDelta, cancellationToken);
                return new ChatExchange(streamedResponse, streamedBody, model);
            }
            catch { streamedResponse.Dispose(); throw; }
        }
        var response = await _http.PostAsync(EndpointUri("chat/completions", endpoint), new StringContent(payloadJson, Encoding.UTF8, "application/json"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode) return new ChatExchange(response, body, model);

        if (IsModelNotFound(body))
        {
            var discovered = await DiscoverModelsAsync(cancellationToken);
            var alias = NormalizeModelAlias(model);
            var matches = discovered.Models.Where(option => NormalizeModelAlias(option.Id) == alias).ToList();
            if (matches.Count == 1)
            {
                response.Dispose();
                var canonicalId = matches[0].Id;
                using var json = JsonDocument.Parse(payloadJson);
                var retry = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
                retry["model"] = canonicalId;
                response = await _http.PostAsync(EndpointUri("chat/completions", endpoint), new StringContent(retry.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Log("model-id-recovered", true, $"Configured ID '{model}' resolved to server ID '{canonicalId}' from {discovered.Source}");
                    return new ChatExchange(response, body, canonicalId);
                }
            }
            else
            {
                Log("model-id-recovery", false, matches.Count == 0
                    ? $"Server does not list configured model ID '{model}'. Source: {discovered.Source}"
                    : $"Model alias '{model}' matched multiple server IDs: {string.Join(", ", matches.Select(item => item.Id))}");
            }
            if (!response.IsSuccessStatusCode)
            {
                if (matches.Count == 0)
                    body += $"\nJarvis konnte die gespeicherte Modell-ID '{model}' nicht in der aktuellen Server-Modellliste finden. Verfügbare IDs: {string.Join(", ", discovered.Models.Select(item => item.Id))}";
            }
            return new ChatExchange(response, body, model);
        }

        if (!IsNoModelLoaded(body)) return new ChatExchange(response, body, model);

        response.Dispose();
        var loadedInstance = await LoadLocalModelForChatAsync(model, endpoint, cancellationToken);
        using (var json = JsonDocument.Parse(payloadJson))
        {
            var retry = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
            retry["model"] = loadedInstance;
            payloadJson = retry.ToJsonString();
        }
        response = await _http.PostAsync(EndpointUri("chat/completions", endpoint), new StringContent(payloadJson, Encoding.UTF8, "application/json"), cancellationToken);
        body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ChatExchange(response, body, loadedInstance);
    }

    private static async Task<string> ReadStreamingChatCompletionAsync(HttpResponseMessage response, Action<string> onTextDelta, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var content = new StringBuilder();
        var streamedCalls = new SortedDictionary<int, JsonObject>();
        var finishReason = "stop";
        JsonNode? usageNode = null;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            JsonNode? eventNode;
            try { eventNode = JsonNode.Parse(data); } catch (JsonException) { continue; }
            if (eventNode is not JsonObject root) continue;
            if (root["error"] is JsonNode errorNode)
                throw new HttpRequestException("Streaming chat endpoint returned an error: " + Trim(errorNode.ToJsonString()));
            if (root["usage"] is JsonNode usage) usageNode = usage.DeepClone();
            if (root["choices"] is not JsonArray choices || choices.Count == 0 || choices[0] is not JsonObject choice) continue;
            if (choice["finish_reason"] is JsonValue reasonValue && reasonValue.TryGetValue<string>(out var reason) && !string.IsNullOrWhiteSpace(reason)) finishReason = reason;
            if (choice["delta"] is not JsonObject delta) continue;
            if (delta["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var textDelta) && !string.IsNullOrEmpty(textDelta))
            {
                content.Append(textDelta);
                onTextDelta(textDelta);
            }
            if (delta["tool_calls"] is not JsonArray toolCalls) continue;
            foreach (var toolNode in toolCalls.OfType<JsonObject>())
            {
                var index = toolNode["index"]?.GetValue<int>() ?? 0;
                if (!streamedCalls.TryGetValue(index, out var accumulated))
                    streamedCalls[index] = accumulated = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                if (toolNode["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id)) accumulated["id"] = id;
                if (toolNode["function"] is JsonObject function)
                {
                    var targetFunction = accumulated["function"]!.AsObject();
                    if (function["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name)) targetFunction["name"] = (targetFunction["name"]?.GetValue<string>() ?? "") + name;
                    if (function["arguments"] is JsonValue argumentValue && argumentValue.TryGetValue<string>(out var arguments)) targetFunction["arguments"] = (targetFunction["arguments"]?.GetValue<string>() ?? "") + arguments;
                }
            }
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() };
        if (streamedCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var pair in streamedCalls)
            {
                pair.Value["id"] ??= "call_" + Guid.NewGuid().ToString("N");
                calls.Add(pair.Value);
            }
            message["tool_calls"] = calls;
        }
        var choiceNode = new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finishReason };
        var result = new JsonObject { ["choices"] = new JsonArray(choiceNode) };
        if (usageNode is not null) result["usage"] = usageNode;
        return result.ToJsonString();
    }

    private static bool IsStreamingUnsupported(string body) =>
        body.Contains("stream", StringComparison.OrdinalIgnoreCase)
        && (body.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("not allowed", StringComparison.OrdinalIgnoreCase));

    private async Task<string> LoadLocalModelForChatAsync(string model, string endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var configuredUri) || !configuredUri.IsLoopback)
            throw new InvalidOperationException("Der Server meldet, dass kein Modell geladen ist. Lade ein Modell auf dem konfigurierten Server und versuche es erneut.");
        var available = _availableModels.FirstOrDefault(option => option.CanLoad && string.Equals(option.Id, model, StringComparison.OrdinalIgnoreCase));
        if (available is null)
        {
            // Some LM Studio versions return an empty catalog while the configured model key
            // is still loadable. Try the documented load API directly before asking the user.
            available = new ChatModelOption(model, model, false, true);
        }
        if (available.IsLoaded) return available.LoadedInstanceId ?? available.Id;

        ModelStatus.Text = $"Lade {available.DisplayName.Split('·')[0].Trim()} in den Arbeitsspeicher …";
        ShowActivity("Jarvis lädt das Modell", $"{available.DisplayName.Split('·')[0].Trim()} wird in LM Studio geladen. Das kann etwas dauern …");
        var loadUri = new UriBuilder(configuredUri!) { Path = "/api/v1/models/load", Query = "", Fragment = "" }.Uri;
        try
        {
            using var loadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var loadResponse = await loadClient.PostAsync(loadUri, new StringContent(JsonSerializer.Serialize(new { model = available.Id }), Encoding.UTF8, "application/json"), cancellationToken);
            var loadBody = await loadResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!loadResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"LM Studio meldet, dass kein Modell geladen ist. Jarvis hat versucht, '{available.Id}' über die lokale Lade-API zu laden, aber diese meldete HTTP {(int)loadResponse.StatusCode}: {Trim(loadBody)}. Lade das Modell im LM-Studio-Tab „Developer“ oder prüfe, ob dieser Modellname im Tab „My Models“ vorhanden ist.");
            using var loadJson = JsonDocument.Parse(loadBody);
            var instanceId = loadJson.RootElement.TryGetProperty("instance_id", out var instance) ? instance.GetString() : null;
            var loadedId = string.IsNullOrWhiteSpace(instanceId) ? available.Id : instanceId;
            var index = _availableModels.FindIndex(option => string.Equals(option.Id, available.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _availableModels[index] = available with { Id = loadedId!, IsLoaded = true, LoadedInstanceId = loadedId, DisplayName = available.DisplayName.Replace("bei Bedarf laden", "geladen", StringComparison.OrdinalIgnoreCase) };
            else _availableModels.Insert(0, available with { Id = loadedId!, IsLoaded = true, LoadedInstanceId = loadedId, DisplayName = $"{available.Id} · geladen" });
            var previousLoading = _loading; _loading = true; ChatModelBox.ItemsSource = _availableModels; ChatModelBox.SelectedValue = loadedId; _loading = previousLoading;
            ModelStatus.Text = "Modell geladen · sende deine Nachricht …";
            Log("model-load", true, $"LM Studio Modell {available.Id} geladen");
            return loadedId!;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"LM Studio hat eine ungültige Antwort beim Laden von '{available.Id}' zurückgegeben: {ex.Message}. Lade das Modell in LM Studio manuell und aktualisiere danach die Modellliste.", ex);
        }
    }

    private static string? FindLmStudioCli()
    {
        var candidates = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(profile)) candidates.Add(Path.Combine(profile, ".lmstudio", "bin", "lms.exe"));
        if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "Programs", "LM Studio", "resources", "app", "bin", "lms.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(directory.Trim('"'), "lms.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunLmStudioCliAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = FindLmStudioCli();
        if (executable is null) throw new InvalidOperationException("Die LM-Studio-CLI 'lms.exe' wurde nicht gefunden. Installiere die CLI in LM Studio oder lade das Modell dort manuell.");
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("LM Studio CLI konnte nicht gestartet werden.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"LM Studio CLI meldet Fehler: {Trim(error.Length > 0 ? error : output)}");
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("Der LM-Studio-Befehl hat das Zeitlimit überschritten.");
        }
    }

    private static async Task<List<ChatModelOption>> ReadLmStudioCliCatalogAsync(CancellationToken cancellationToken)
    {
        var output = await RunLmStudioCliAsync(["ls", "--json"], TimeSpan.FromSeconds(12), cancellationToken);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var data = root.ValueKind == JsonValueKind.Array ? root :
            (root.TryGetProperty("models", out var models) ? models : root.TryGetProperty("data", out var items) ? items : default);
        if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("LM Studio CLI hat kein Modell-Array zurückgegeben.");
        return data.EnumerateArray().Select(item =>
            {
                var id = FirstString(item, "key", "model_key", "modelKey", "id");
                var name = FirstString(item, "display_name", "displayName", "name", "model_name") ?? id;
                return string.IsNullOrWhiteSpace(id) ? null : new ChatModelOption(id, $"{name} · bei Bedarf laden", false, true);
            }).Where(option => option is not null).Cast<ChatModelOption>()
            .DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return null;
    }
    private string BuildSystemContext()
    {
        var prompt = string.IsNullOrWhiteSpace(_config.SystemPrompt) ? DefaultPrompt : _config.SystemPrompt.Trim();
        prompt += "\n\nSPRACHE: Antworte immer auf Deutsch, unabhängig von der Sprache der App oder der Eingabe. Wechsle nur dann zu Englisch, wenn der Nutzer ausdrücklich darum bittet, auf Englisch zu antworten.";
        if (_voiceModeEnabled) prompt += "\n\nThis is a live voice conversation. Keep spoken replies natural, direct, and brief (usually one or two sentences). Avoid markdown, headings, long lists, and unnecessary preambles. Ask at most one short clarification only when needed.";
        prompt += "\n\nWERKZEUG- UND AUFGABENABLAUF\nVerfügbare Werkzeuge: plan_task, list_files, search_files, read_file, create_folder, write_file, request_folder_access, open_app, open_folder, open_url, google_search, spotify_control, wait, list_windows, close_window, move_window, maximize_window. Bei klaren Nutzeraufträgen führe die nötigen Tool-Aufrufe direkt aus; vermeide doppelte verbale Rückfragen, wenn die App bereits die Freigabe inline anzeigt. Verwende plan_task bei mehrstufigen oder längeren Aufträgen: teile sie in wenige überprüfbare Schritte und zeige diesen Plan im Chat. Ein Plan ist nur ein Plan und kein Beleg einer Ausführung. Führe die Schritte dann der Reihe nach mit passenden Tools aus, zeige kurze Fortschrittszusammenfassungen, lies jedes Ergebnis und passe den nächsten Schritt an. Die App erlaubt höchstens 20 Modellrunden und 48 Werkzeugaktionen. Beende einen offenen Plan nicht mit einer voreiligen Textantwort: arbeite offene ausführbare Schritte weiter ab. Bei Fehlern stoppe nur davon abhängige Schritte, korrigiere sicher, setze unabhängige Schritte fort und berichte jedes nicht lösbare Hindernis. Nutze wait nur für explizit gewünschte Verzögerungen. Schließe Fenster nur mit close_window; es sendet eine normale WM_CLOSE-Anforderung. Verschiebe Fenster mit move_window nur nach inline Freigabe. Vergrößere Fenster mit maximize_window in den normalen Windows-Maximierzustand; das ist kein exklusiver Vollbildmodus. list_windows liefert reale Titel, Prozessnamen und Monitorzuordnung; bei Mehrdeutigkeit nicht raten. open_app startet keine Installer oder Uninstaller; YouTube immer im Standardbrowser. Dateizugriff bleibt auf freigegebene Ordner beschränkt; bei neuem Ziel zuerst request_folder_access, danach weiterarbeiten. Schreibvorgänge brauchen eine Vorschau-Freigabe. Google-Suche liefert echte, relevante Links und Ausschnitte; Google wird zuerst versucht, Bing/DuckDuckGo-Fallbacks werden als solche benannt. Fasse Suchergebnisse in eigenen Worten knapp zusammen, beantworte die Frage direkt und verlinke passende Quellen; kopiere weder alle Snippets noch das rohe Tool-Ergebnis in die Antwort. Generische Homepage-Treffer sind keine Antwort auf eine Faktenfrage. Falls die Suche unzureichend ist, sage das und erfinde keine Suchergebnisse. Spotify-Lautstärke bezieht sich ausschließlich auf die Spotify-Audiositzung. Verwende get_volume zum Lesen und set_volume für einen Zielwert; die App muss den Istwert verifizieren. volume_sequence merkt den echten Anfangswert, setzt 100 %, wartet, fährt schrittweise auf 0 % und stellt den Anfangswert wieder her. Bei fehlender Audiositzung keine globale Systemlautstärke verändern. Play/Pause/Next/Previous senden Windows-Medientasten an die aktive Sitzung und sind nicht verifiziert. Spotify-Titelaktionen und Playliständerungen werden durch spotify_control.play_track, like_current und add_to_playlist über die in Einstellungen > Musik autorisierte Spotify-Verbindung ausgeführt. Bei Song-zu-Playlist-Aufträgen rufe add_to_playlist mit query=Songtitel und playlist=Playlistname auf; behaupte niemals, das Konto sei nicht verbunden, bevor das Tool seine tatsächliche Rückgabe geliefert hat. Bei fehlendem Client ID, ungültiger Autorisierung, fehlendem Scope oder API-Fehler nenne exakt den vom Tool gemeldeten Grund. Apple Music, Discord-Call-Steuerung, Kamera, Bildschirmübertragung und Headset-Schalter sind derzeit nicht implementiert. Jarvis-App-Hilfe: Profil unten links; Chatverlauf links; KI-Verbindung/Systemprompt unter Settings > AI connection (de: Einstellungen > KI-Verbindung); Freigaben unter Privacy & tools (de: Datenschutz & Werkzeuge); Mikrofon/Whisper unter Speech input (de: Spracheingabe). Entwicklerin der Jarvis-Anwendung: Felix FGF. Den Anbieter des zugrunde liegenden Modells nur nennen, wenn er sich aus Modellname oder Endpunkt sicher ergibt. Zeige Status und knappe sichtbare Zwischenschritte, aber keine verborgenen internen Gedankengänge. Erfolg nur nach bestätigtem Toolresultat behaupten.";
        prompt += "\n\nSTRIKTE AUSFÜHRUNG VON AUFGABENPLÄNEN\nWenn ein Auftrag mindestens zwei voneinander abhängige Aktionen braucht, rufe zuerst plan_task mit einem kurzen Titel und 1 bis 10 konkreten, geordneten Schritten auf. Plane jeden Schritt so, dass er genau einem ausführbaren Werkzeugaufruf entspricht. Führe danach die Werkzeuge in dieser Reihenfolge aus und lies jeweils das echte Ergebnis, bevor du den nächsten Schritt beginnst. Jarvis zeigt den Plan als sichtbare Chat-Checkliste und aktualisiert den Status automatisch: in Arbeit vor dem Aufruf, abgeschlossen ausschließlich bei ok:true, fehlgeschlagen bei ok:false. Bei einem Fehler prüfe die Rückgabe, versuche eine konkrete sichere Korrektur und fahre mit unabhängigen offenen Schritten fort; nur abhängige Schritte bleiben blockiert. Nenne am Ende klar, was abgeschlossen, fehlgeschlagen oder noch offen ist. Erfinde keine Schritte oder erfolgreichen Ergebnisse. Gib nur kurze Fortschrittszusammenfassungen aus, keine verborgenen Gedankengänge.\n\nSPIELE UND INSTALLIERTE APPS\nWenn der Nutzer ein Spiel beim Namen öffnen lässt, verwende open_app mit dem exakten Spielnamen. Jarvis prüft zuerst Epic-Manifeste, Steam-Bibliotheken und installierte Riot-VALORANT-Pfade; dabei wird die echte ausführbare Datei gesucht und dem Nutzer in der einmaligen Freigabe angezeigt. Steam-Spiele werden bevorzugt über die offizielle Steam-Spiel-URI gestartet, damit DRM und Abhängigkeiten funktionieren; Epic-/Riot-Spiele über ihre gefundene Programmdatei beziehungsweise den Launcher. Wenn ein konkreter Fund scheitert, nutze die vom Werkzeug zurückgemeldeten Installationsdaten und berichte den nächsten sicheren Versuch, statt einen geratenen Pfad zu erfinden. Keine Deinstallierer, Installer oder Shell-Befehle starten.";
        prompt += "\n\nVERBINDLICHE TOOL-AUSFÜHRUNG – KEINE ERFUNDENEN AKTIONEN\nWenn der Nutzer eine konkrete Aktion verlangt, für die ein passendes Werkzeug in der aktuellen Werkzeugliste verfügbar ist, rufe dieses Werkzeug tatsächlich auf. Jarvis markiert erkannte Aktionsaufträge zusätzlich technisch mit tool_choice=required, damit die Auswahl nicht dem optionalen auto-Modus überlassen bleibt. Antworte nicht nur mit einer Ankündigung oder einer Behauptung wie „erledigt“, „geöffnet“, „gesucht“, „gespeichert“ oder „geändert“. Eine Absicht, ein Plan, eine Tool-Beschreibung oder ein natürlichsprachiger Satz ist niemals ein Tool-Aufruf. Prüfe nach dem Aufruf die echte Rückgabe: nur ok:true bestätigt Erfolg; ok:false, Fehler, Timeout, Ablehnung oder eine fehlende Rückgabe bedeuten, dass die Aktion nicht als erledigt gemeldet werden darf. Bei Fehlern sage knapp, was tatsächlich fehlgeschlagen ist und was noch offen ist. Für Recherche verwende google_search und nenne nur Ergebnisse, die wirklich zurückgegeben wurden; fasse sie in eigenen Worten knapp zusammen, kopiere weder Tool-JSON noch alle Snippets. Für Apps/Spiele open_app, für Fenster list_windows und danach close_window/move_window/maximize_window, für Spotify spotify_control und für mehrstufige Aufgaben zuerst plan_task. Wenn Modell oder Server tool_choice=required nicht unterstützen, keine Aktion ausführen und offen sagen, dass der Aufruf nicht möglich war. Erfinde weder Tool-Ergebnisse noch eine erfolgreiche Ausführung.";
        prompt += "\n\nMUSIKVERBINDUNGEN: spotify_control.play_track, like_current und add_to_playlist verwenden Spotify OAuth, sobald der Nutzer das Konto unter Einstellungen > Musik verbunden hat. Die OAuth-Autorisierung wird durch den Nutzer erteilt; die App speichert nur den DPAPI-geschützten Refresh-Token. Spotify playback API benötigt gegebenenfalls Premium und einen aktiven Player. apple_music_control verwendet die in Einstellungen > Musik eingetragenen Apple Developer- und Music User-Tokens. Die Apple-Wiedergabe wird über Windows extern geöffnet und kann von Jarvis nicht als tatsächlich laufend verifiziert werden. Favoriten und Playliständerungen nur nach erfolgreicher Serverantwort bestätigen.";
        prompt += "\n\nFreigegebene Ordner: " + (_config.AllowedRoots.Count == 0 ? "keine" : string.Join("; ", _config.AllowedRoots)) + ". Bei einem Ziel außerhalb dieser Freigaben verwende zuerst request_folder_access für den existierenden Zielordner. Behaupte niemals, Ordnerzugriff zu haben, bevor der Nutzer die Chatfreigabe angenommen hat. Frage nicht zusätzlich in Textform nach Ja/Nein, bevor du open_url oder open_app mit der eingebauten Freigabe ausführst.";
        if (!string.IsNullOrWhiteSpace(_activeChat?.Workspace)) prompt += $"\n\nAktiver Arbeitsordner dieses Chats: {_activeChat.Workspace}. Verwende für Dateiwerkzeuge ausschließlich diesen Arbeitsordner.";
        var memory = _memories.Where(m => m.Confirmed).Select(m => $"- [{m.Category}] {m.Content}").ToList();
        var context = string.Join("\n", memory).Trim();
        var systemContext = context.Length == 0 ? prompt : prompt + "\n\nVom Nutzer bestätigte gespeicherte Informationen (nur falls relevant verwenden; keine weiteren Schlüsse daraus erfinden):\n" + (context.Length > 5000 ? context[..5000] : context);
        // Enforce this application policy even when the user has a custom saved prompt.
        // Keeping it last gives the requirement priority over conflicting prompt text.
        systemContext += "\n\nVERBINDLICHE PLAN-FORTSETZUNG UND VERIFIKATION\nEin sichtbarer Plan ist ein aktiver Arbeitsauftrag und darf nicht durch eine voreilige Abschlussantwort beendet werden. Führe alle noch ausführbaren offenen Schritte nacheinander mit echten Werkzeugaufrufen fort und prüfe jede echte Rückgabe. Wenn ein Werkzeug fehlschlägt, lies den Fehler, versuche eine konkrete sichere Korrektur oder Alternative und fahre danach mit unabhängigen Schritten fort. Führe keine Schritte aus, deren Voraussetzungen fehlgeschlagen sind. Nur Nutzerabbruch, abgelehnte oder fehlende Freigabe, nicht verfügbare Fähigkeiten, ein nicht behebbares Hindernis oder das technische Ausführungslimit darf den Plan beenden; nenne dann genau den Grund und alle offenen Punkte. Bei Programmieraufträgen untersuche relevante Dateien, ändere sie, lies die Änderungen erneut und führe verfügbare Build-, Test- oder Laufzeitprüfungen aus. Behebe dabei gefundene Fehler und wiederhole die Prüfung. Behaupte niemals, Tests oder Builds ausgeführt zu haben, wenn kein entsprechender Tool-Aufruf tatsächlich erfolgt ist; wenn die aktuelle Werkzeugliste keine Ausführung erlaubt, benenne diese Grenze ausdrücklich. Fortschritt kurz und sichtbar zusammenfassen, keine verborgenen Gedankengänge ausgeben.";
        return systemContext + "\n\nVERBINDLICHE ANTWORTSPRACHE\nAntworte immer auf Deutsch, auch wenn die App-Oberfläche auf Englisch eingestellt ist oder die Nutzernachricht Englisch enthält. Verwende Englisch ausschließlich, wenn der Nutzer ausdrücklich eine englische Antwort verlangt.\n\nVERBINDLICHE PLANREGEL DER JARVIS-ANWENDUNG\nWenn die Aufgabe zwei oder mehr Tool-Aufrufe benötigt oder absehbar benötigen kann, ist plan_task immer der allererste Tool-Aufruf. Das gilt für abhängige UND unabhängige Schritte sowie für jedes ausgewählte Modell. Rufe plan_task vor jedem anderen Tool auf, zeige den Plan im Chat und führe die Schritte anschließend einzeln und in Reihenfolge aus. Keine Aktions-Tools vor dem Plan, keine parallelen Aktionsaufrufe und keine Ausnahme wegen Modellverhalten. Wird ein weiterer Tool-Aufruf erst später erforderlich, erstelle zuerst mit plan_task einen Plan für die verbleibenden Aufrufe. Einfache Fragen ohne Tools und einzelne Aktionen mit genau einem Tool brauchen keinen Plan. Berichte Planfortschritt nur anhand tatsächlicher Tool-Ergebnisse; ein Plan ist keine Ausführung.";
    }

    private void AddMessage(string role, string text, bool persist = true, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        var message = new ChatRecord(role, text, attachments?.ToList());
        var panel = new StackPanel { Margin = role == "user" ? new Thickness(100, 9, 16, 9) : new Thickness(0, 9, 0, 9), Tag = message, MaxWidth = role == "user" ? 590 : 760, HorizontalAlignment = role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var content = new StackPanel();
        if (attachments is { Count: > 0 }) content.Children.Add(new TextBlock { Text = "📎  " + string.Join("  ·  ", attachments.Select(a => a.Name)), Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (role != "plan") RenderMarkdown(content, text);
        if (role == "plan")
        {
            panel.Children.Add(new TextBlock { Text = "PLAN", Foreground = (Brush)FindResource("Accent"), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
            panel.Children.Add(CreatePlanCard(text));
        }
        else if (role == "system")
        {
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var logs = new Button { Content = "Logs", ToolTip = "Logs anzeigen", Tag = message, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(255, 205, 211)), Background = new SolidColorBrush(Color.FromRgb(76, 31, 39)), BorderBrush = new SolidColorBrush(Color.FromRgb(139, 62, 73)), VerticalAlignment = VerticalAlignment.Center };
            logs.Click += ShowLogs_Click;
            DockPanel.SetDock(logs, Dock.Right); header.Children.Add(logs);
            header.Children.Add(new TextBlock { Text = "STATUS", Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 145)), FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(header);
            panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(48, 25, 30)), BorderBrush = new SolidColorBrush(Color.FromRgb(114, 49, 59)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Child = content });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = role switch { "user" => "DU", "assistant" => "JARVIS", "tool" => "TOOL", _ => "STATUS" }, Foreground = role == "assistant" ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
            if (role == "user") panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(37, 38, 49)), CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 13, 18, 13), MaxWidth = 590, HorizontalAlignment = HorizontalAlignment.Right, Child = content });
            else if (role == "tool")
            {
                var parts = text.Split('\n', 2);
                var output = parts.Length > 1 ? parts[1] : parts[0];
                try { using var jsonOutput = JsonDocument.Parse(output); output = JsonSerializer.Serialize(jsonOutput.RootElement, new JsonSerializerOptions { WriteIndented = true }); } catch (JsonException) { }
                var details = new TextBox { Text = output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, MinHeight = 42, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = (Brush)FindResource("Field"), BorderThickness = new Thickness(0), Padding = new Thickness(10), FontSize = 11 };
                var detailBorder = new Border { Child = details, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(25, 26, 33)), CornerRadius = new CornerRadius(0, 0, 8, 8), Padding = new Thickness(7, 0, 7, 7) };
                var chevron = new TextBlock { Text = "›", FontSize = 19, Foreground = (Brush)FindResource("Muted"), Width = 22, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new System.Windows.Point(.5, .5), RenderTransform = new RotateTransform(0) };
                var title = new TextBlock { Text = parts.Length > 1 ? parts[0] : "Tool-Ergebnis", FontSize = 12, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center };
                var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; titleRow.Children.Add(chevron); titleRow.Children.Add(title);
                var toggle = new Button { Content = titleRow, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(5, 4, 6, 4) };
                toggle.Click += (_, _) =>
                {
                    var expanded = detailBorder.Visibility != Visibility.Visible;
                    detailBorder.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                    ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(expanded ? 0 : 90, expanded ? 90 : 0, TimeSpan.FromMilliseconds(130)));
                };
                var toolCard = new Border { Background = new SolidColorBrush(Color.FromRgb(25, 26, 33)), BorderBrush = new SolidColorBrush(Color.FromRgb(54, 55, 68)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(5), Child = new StackPanel() };
                ((StackPanel)toolCard.Child).Children.Add(toggle); ((StackPanel)toolCard.Child).Children.Add(detailBorder); panel.Children.Add(toolCard);
            }
            else panel.Children.Add(content);
        }
        if (role == "assistant")
        {
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            actions.Children.Add(CreateMessageAction("⧉", "Antwort kopieren", CopyMessage_Click, message));
            actions.Children.Add(CreateMessageAction("↻", "Antwort erneut generieren", RegenerateMessage_Click, message));
            var speak = CreateMessageAction("◖))", "Antwort lokal vorlesen", TtsMessage_Click, message);
            actions.Children.Add(speak);
            var stopSpeech = CreateMessageAction("■", "Vorlesen stoppen", TtsStop_Click, message); stopSpeech.Visibility = Visibility.Collapsed;
            actions.Children.Add(stopSpeech);
            panel.Children.Add(actions);
        }
        Messages.Children.Add(panel);
        ChatStartPanel.Visibility = Visibility.Collapsed;
        WorkspaceHeader.Visibility = Visibility.Collapsed;
        if (persist)
        {
            panel.Opacity = 0;
            panel.RenderTransform = new TranslateTransform(0, 8);
            panel.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
            panel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(190)));
        }
        ChatScroll.ScrollToEnd();
    }

    private Border CreatePlanCard(string json)
    {
        TaskExecutionPlan? plan = null;
        try { plan = JsonSerializer.Deserialize<TaskExecutionPlan>(json, JsonOptions); } catch (JsonException) { }
        var content = new StackPanel();
        if (plan is null)
        {
            content.Children.Add(new TextBlock { Text = json, Foreground = (Brush)FindResource("Text"), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            var german = _config.UiLanguage == "de";
            content.Children.Add(new TextBlock { Text = plan.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) });
            content.Children.Add(new TextBlock { Text = german ? PlanStatusGerman(plan.Status) : plan.Status, FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 7) });
            foreach (var step in plan.Steps)
            {
                var color = step.Status switch
                {
                    "Completed" => new SolidColorBrush(Color.FromRgb(112, 204, 153)),
                    "In progress" => (Brush)FindResource("AccentStrong"),
                    "Failed" => new SolidColorBrush(Color.FromRgb(255, 130, 145)),
                    _ => (Brush)FindResource("Muted")
                };
                var state = step.Status switch { "Completed" => "✓", "In progress" => "◌", "Failed" => "×", "Not run" => "–", _ => "○" };
                var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = $"{state}  {step.Number}. ", Foreground = color, FontWeight = FontWeights.SemiBold, MinWidth = 49 });
                var details = new StackPanel();
                details.Children.Add(new TextBlock { Text = step.Description, Foreground = (Brush)FindResource("Text"), TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(step.Detail)) details.Children.Add(new TextBlock { Text = step.Detail, Foreground = (Brush)FindResource("Muted"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                line.Children.Add(details);
                content.Children.Add(new Border { Child = line, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Color.FromRgb(28, 29, 38)), CornerRadius = new CornerRadius(7) });
            }
        }
        return new Border { Child = content, Background = new SolidColorBrush(Color.FromRgb(25, 26, 33)), BorderBrush = new SolidColorBrush(Color.FromRgb(76, 68, 126)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private static string PlanStatusGerman(string status) => status switch
    {
        "In progress" => "Wird Schritt für Schritt ausgeführt", "Completed" => "Alle Schritte abgeschlossen", "Failed" => "Gestoppt · ein Schritt ist fehlgeschlagen", "Incomplete" => "Unvollständig · nicht alle Schritte ausgeführt", "Cancelled" => "Abgebrochen", _ => status
    };

    private TaskExecutionPlanStep? BeginPlanStep(string action)
    {
        if (_activePlan is null || _activePlan.Status != "In progress") return null;
        var step = _activePlan.Steps.FirstOrDefault(candidate => candidate.Status == "Pending");
        if (step is null) return null;
        step.Status = "In progress";
        step.Detail = _config.UiLanguage == "de" ? $"Läuft: {action}" : $"Running: {action}";
        RefreshPlanMessage();
        AddTask("Plan: " + _activePlan.Title, $"Schritt {step.Number}/{_activePlan.Steps.Count}", action);
        Log("plan-progress", true, $"{_activePlan.Id}; step={step.Number}; started={action}");
        return step;
    }

    private void CompletePlanStep(TaskExecutionPlanStep step, bool succeeded, string result)
    {
        if (_activePlan is null) return;
        step.Status = succeeded ? "Completed" : "Failed";
        step.Detail = result;
        RefreshPlanMessage();
        AddTask("Plan: " + _activePlan.Title, succeeded ? $"Schritt {step.Number} abgeschlossen" : $"Schritt {step.Number} fehlgeschlagen", result);
        Log("plan-progress", succeeded, $"{_activePlan.Id}; step={step.Number}; {result}");
    }

    private void FinishActivePlan()
    {
        if (_activePlan is null || _activePlan.Status != "In progress") return;
        var pending = _activePlan.Steps.Where(step => step.Status is "Pending" or "In progress").ToList();
        foreach (var step in pending)
        {
            step.Status = "Not run";
            step.Detail = _config.UiLanguage == "de" ? "Vor der endgültigen Antwort nicht ausgeführt." : "Not executed before the final response.";
        }
        var hasFailedStep = _activePlan.Steps.Any(step => step.Status == "Failed");
        _activePlan.Status = pending.Count == 0 && !hasFailedStep ? "Completed" : "Incomplete";
        RefreshPlanMessage();
        AddTask("Plan: " + _activePlan.Title, _activePlan.Status == "Completed" ? "Abgeschlossen" : "Unvollständig", $"{_activePlan.Steps.Count(s => s.Status == "Completed")}/{_activePlan.Steps.Count} Schritte abgeschlossen.");
    }

    private void FailActivePlan(string detail, string status)
    {
        if (_activePlan is null || _activePlan.Status == "Failed") return;
        var current = _activePlan.Steps.FirstOrDefault(step => step.Status == "In progress");
        if (current is not null) { current.Status = status; current.Detail = detail; }
        foreach (var step in _activePlan.Steps.Where(step => step.Status == "Pending")) { step.Status = "Not run"; step.Detail = _config.UiLanguage == "de" ? "Wegen des vorherigen Fehlers nicht ausgeführt." : "Not run because an earlier step failed."; }
        _activePlan.Status = status;
        RefreshPlanMessage();
        AddTask("Plan: " + _activePlan.Title, status == "Cancelled" ? "Abgebrochen" : "Fehlgeschlagen", detail);
        Log("plan-progress", false, $"{_activePlan.Id}; {status}; {detail}");
    }

    private void RefreshPlanMessage()
    {
        if (_activePlan is null) return;
        var old = Messages.Children.OfType<StackPanel>().FirstOrDefault(panel => panel.Tag is ChatRecord record && record.Role == "plan" && PlanMessageId(record.Text) == _activePlan.Id);
        if (old is not null) Messages.Children.Remove(old);
        AddMessage("plan", JsonSerializer.Serialize(_activePlan, JsonOptions));
        if (_activeChat is not null) _ = SaveChat();
    }

    private static string? PlanMessageId(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.TryGetProperty("Id", out var id) ? id.GetString() : document.RootElement.TryGetProperty("id", out id) ? id.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private static bool ReadToolSuccess(string result)
    {
        try { using var document = JsonDocument.Parse(result); return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }

    private static string SummarizeToolResult(string result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) return Trim(error.GetString() ?? "Werkzeug fehlgeschlagen.");
            var root = document.RootElement;
            var parts = new List<string>();
            foreach (var key in new[] { "provider", "query", "application", "platform", "executable", "launch_target", "path", "percent", "verified", "waited_seconds" })
                if (root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    parts.Add(key + "=" + (value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()));
            foreach (var key in new[] { "results", "entries", "windows", "closed" })
                if (root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array) parts.Add(key + "=" + list.GetArrayLength());
            return parts.Count == 0 ? "Werkzeug erfolgreich (ok:true)." : Trim(string.Join(" · ", parts));
        }
        catch (JsonException) { }
        return Trim(result.Replace('\r', ' ').Replace('\n', ' '));
    }

    private void ShowActivity(string title, string detail)
    {
        if (_activityPanel is null)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = (Brush)FindResource("AccentStrong"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            _activityTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text") };
            _activityDetail = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            var labels = new StackPanel(); labels.Children.Add(_activityTitle); labels.Children.Add(_activityDetail);
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; row.Children.Add(dot); row.Children.Add(labels);
            _activityPanel = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 29, 38)), BorderBrush = new SolidColorBrush(Color.FromRgb(55, 53, 75)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 14, 9), Margin = new Thickness(0, 8, 0, 10), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0 };
            _activityPanel.Child = row;
            _activityPanel.RenderTransform = new TranslateTransform(0, 5);
            _activityPanel.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            ((TranslateTransform)_activityPanel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180)));
        }
        _activityTitle!.Text = title;
        _activityDetail!.Text = detail;
        Messages.Children.Remove(_activityPanel);
        Messages.Children.Add(_activityPanel);
        ChatScroll.ScrollToEnd();
    }

    private void HideActivity()
    {
        if (_activityPanel is null) return;
        Messages.Children.Remove(_activityPanel);
        _activityPanel.BeginAnimation(OpacityProperty, null);
        _activityTitle = null; _activityDetail = null; _activityPanel = null;
    }

    private async Task<(bool Approved, string Feedback)> RequestApprovalInlineAsync(string title, string details, CancellationToken token, string? trustScope = null)
    {
        var german = _config.UiLanguage == "de";
        var trustKey = ApprovalKey(title, trustScope ?? details);
        if (_config.TrustedApprovals.Any(approval => string.Equals(approval.Key, trustKey, StringComparison.Ordinal)))
        {
            Log("approval", true, $"Dauerhafte Freigabe verwendet: {title}; scope={(trustScope ?? "exact-action")}");
            return (true, "");
        }
        var completion = new TaskCompletionSource<(bool Approved, string Feedback)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heading = new TextBlock { Text = Jarvis.Services.UiLocalization.Translate(title, _config.UiLanguage), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text"), TextWrapping = TextWrapping.Wrap };
        var description = new TextBlock { Text = details, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        var feedback = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 100, Visibility = Visibility.Collapsed, ToolTip = german ? "Was soll Jarvis stattdessen tun?" : "What should Jarvis do instead?", Margin = new Thickness(0, 6, 0, 4) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var accept = new Button { Content = german ? "Annehmen" : "Accept", Padding = new Thickness(13, 7, 13, 7), Background = new SolidColorBrush(Color.FromRgb(55, 72, 63)), BorderBrush = new SolidColorBrush(Color.FromRgb(76, 120, 92)), Margin = new Thickness(0, 2, 6, 2) };
        var reject = new Button { Content = german ? "Ablehnen" : "Reject", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 2, 6, 2) };
        var rejectWithNote = new Button { Content = german ? "Ablehnen + Hinweis" : "Reject + note", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 2, 6, 2) };
        var trust = new Button { Content = german ? "Annehmen & immer vertrauen" : "Accept and always trust", Padding = new Thickness(13, 7, 13, 7), Background = new SolidColorBrush(Color.FromRgb(45, 53, 70)), Margin = new Thickness(0, 2, 6, 2) };
        var sendNote = new Button { Content = german ? "Hinweis senden" : "Send note", Padding = new Thickness(13, 7, 13, 7), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 6, 2) };
        var state = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        accept.Click += (_, _) => completion.TrySetResult((true, ""));
        reject.Click += (_, _) => completion.TrySetResult((false, ""));
        rejectWithNote.Click += (_, _) => { feedback.Visibility = Visibility.Visible; sendNote.Visibility = Visibility.Visible; rejectWithNote.Visibility = Visibility.Collapsed; feedback.Focus(); };
        trust.Click += async (_, _) =>
        {
            try
            {
                _config.TrustedApprovals.RemoveAll(approval => string.Equals(approval.Key, trustKey, StringComparison.Ordinal));
                _config.TrustedApprovals.Add(new TrustedApprovalRecord(trustKey, title, trustScope is null ? details : (german ? $"Vertrauensbereich: {trustScope}. Details je Ausführung werden weiterhin angezeigt." : $"Trust scope: {trustScope}. Per-run details remain visible.")));
                await PersistConfig(); RefreshTrustedApprovals();
                completion.TrySetResult((true, ""));
            }
            catch (Exception ex) { state.Text = (_config.UiLanguage == "de" ? "Vertrauen konnte nicht gespeichert werden: " : "Could not save trust: ") + ex.Message; state.Visibility = Visibility.Visible; Log("approval-trust", false, ex.ToString()); }
        };
        sendNote.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(feedback.Text)) { feedback.ToolTip = german ? "Schreibe kurz, was Jarvis stattdessen tun soll." : "Briefly say what Jarvis should do instead."; feedback.Focus(); return; }
            completion.TrySetResult((false, feedback.Text.Trim()));
        };
        buttons.Children.Add(accept); buttons.Children.Add(trust); buttons.Children.Add(reject); buttons.Children.Add(rejectWithNote); buttons.Children.Add(sendNote);
        var content = new StackPanel(); content.Children.Add(heading); content.Children.Add(description); content.Children.Add(feedback); content.Children.Add(buttons); content.Children.Add(state);
        var card = new Border { Background = new SolidColorBrush(Color.FromRgb(30, 30, 41)), BorderBrush = new SolidColorBrush(Color.FromRgb(95, 81, 151)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(15, 12, 15, 12), Margin = new Thickness(0, 8, 0, 10), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, RenderTransform = new TranslateTransform(0, 8), Child = content };
        card.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(190)));
        Messages.Children.Add(card); ChatStartPanel.Visibility = Visibility.Collapsed; WorkspaceHeader.Visibility = Visibility.Collapsed; ChatScroll.ScrollToEnd();
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        try
        {
            var result = await completion.Task;
            accept.IsEnabled = trust.IsEnabled = reject.IsEnabled = rejectWithNote.IsEnabled = sendNote.IsEnabled = false;
            feedback.IsReadOnly = true; state.Text = result.Approved ? (german ? "Aktion angenommen" : "Action accepted") : result.Feedback.Length > 0 ? (german ? "Abgelehnt · Hinweis an Jarvis gesendet" : "Rejected · note sent to Jarvis") : (german ? "Aktion abgelehnt" : "Action rejected"); state.Visibility = Visibility.Visible;
            Log("approval", result.Approved, $"{title}; {(result.Approved ? "angenommen" : "abgelehnt")}; Hinweis: {result.Feedback}");
            return result;
        }
        catch (OperationCanceledException)
        {
            accept.IsEnabled = trust.IsEnabled = reject.IsEnabled = rejectWithNote.IsEnabled = sendNote.IsEnabled = false;
            state.Text = "Anfrage abgebrochen"; state.Visibility = Visibility.Visible; throw;
        }
    }

    private async Task<string> GoogleSearchAsync(string query, CancellationToken token)
    {
        var providerQuery = Jarvis.Services.GoogleSearchParser.ExpandQuery(query);
        string googleIssue = "Google hat keine Treffer geliefert.";
        try
        {
            using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9,en;q=0.7");
            var searchUri = new Uri("https://www.google.com/search?gbv=1&q=" + Uri.EscapeDataString(providerQuery) + "&num=10");
            using var response = await client.GetAsync(searchUri, HttpCompletionOption.ResponseContentRead, token);
            var html = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Google meldet HTTP {(int)response.StatusCode}.");
            if (Jarvis.Services.GoogleSearchParser.IsConsentPage(html)) throw new InvalidOperationException("Google verlangt eine Consent-Seite.");
            if (Jarvis.Services.GoogleSearchParser.IsBlockedPage(html)) throw new InvalidOperationException("Google hat die automatische Suche blockiert.");
            token.ThrowIfCancellationRequested();
            var results = Jarvis.Services.GoogleSearchParser.KeepRelevant(query, Jarvis.Services.GoogleSearchParser.Parse(html, 12));
            if (results.Count > 0) return JsonSerializer.Serialize(new { ok = true, provider = "Google", query, results });
            googleIssue = $"Google lieferte keine erkennbaren Treffer (HTTP {(int)response.StatusCode}, {html.Length} Zeichen).";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { googleIssue = ex.Message; }

        // Search provider fallback chain. Provider names are returned explicitly; never label fallback hits as Google.
        var fallbackSources = new (string Provider, string Url, Func<string, int, List<GoogleSearchResult>> Parse)[]
        {
            ("Google News (RSS-Fallback)", "https://news.google.com/rss/search?q=" + Uri.EscapeDataString(providerQuery) + "&hl=de&gl=DE&ceid=DE:de", Jarvis.Services.GoogleSearchParser.ParseBingRss),
            ("Bing (RSS-Fallback)", "https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(providerQuery), Jarvis.Services.GoogleSearchParser.ParseBingRss),
            ("Bing (Fallback)", "https://www.bing.com/search?q=" + Uri.EscapeDataString(providerQuery) + "&count=10", Jarvis.Services.GoogleSearchParser.ParseBing),
            ("DuckDuckGo (Fallback)", "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(providerQuery), Jarvis.Services.GoogleSearchParser.ParseDuckDuckGo),
            ("Wikipedia API (Fakten-Fallback)", "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&utf8=1&srsearch=" + Uri.EscapeDataString(Jarvis.Services.GoogleSearchParser.ExpandWikipediaQuery(query)), Jarvis.Services.GoogleSearchParser.ParseWikipediaSearch)
        };
        var fallbackResults = await Task.WhenAll(fallbackSources.Select(async source =>
        {
            try
            {
                using var fallback = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
                fallback.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
                fallback.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9,en;q=0.7");
                using var response = await fallback.GetAsync(source.Url, HttpCompletionOption.ResponseContentRead, token);
                var html = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                var results = Jarvis.Services.GoogleSearchParser.KeepRelevant(query, source.Parse(html, 12));
                return (source.Provider, Results: results, Error: results.Count == 0 ? "keine relevanten Treffer erkannt" : "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (source.Provider, Results: new List<GoogleSearchResult>(), Error: ex.Message); }
        }));
        var selectedFallback = fallbackResults
            .Where(result => result.Results.Count > 0)
            .OrderBy(result => Jarvis.Services.GoogleSearchParser.IsCreationDateQuery(query)
                ? result.Provider.StartsWith("Wikipedia", StringComparison.Ordinal) ? 0 : result.Provider.StartsWith("Google News", StringComparison.Ordinal) ? 1 : 2
                : result.Provider.StartsWith("Google News", StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(result => result.Results.Count)
            .FirstOrDefault();
        if (selectedFallback.Results is { Count: > 0 })
            return JsonSerializer.Serialize(new { ok = true, provider = selectedFallback.Provider, query, google_note = googleIssue, results = selectedFallback.Results });
        var failure = $"Google-Suche lieferte keine relevanten Treffer: {googleIssue} Fallbacks: {string.Join("; ", fallbackResults.Select(result => result.Provider + ": " + result.Error))}";
        Log("google_search", false, $"query={Trim(query)}; {failure}");
        throw new InvalidOperationException(failure);
    }
    private static string HtmlToPlainText(string html)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    private static string SummarizeToolAction(string name, string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            string Read(string key) => doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var subject = name switch
            {
                "open_app" => Read("app_name"),
                "open_url" => Read("url"),
                "google_search" => Read("query"),
                "spotify_control" => Read("query"),
                "apple_music_control" => Read("query"),
                "plan_task" => Read("title"),
                "request_folder_access" => Read("path"),
                "search_files" => Read("pattern"),
                "list_files" or "read_file" => Read("path"),
                _ => ""
            };
            var action = name switch
            {
                "open_app" => "Öffne App",
                "open_url" => "Öffne Webseite",
                "google_search" => "Suche bei Google",
                "spotify_control" => "Steuere Spotify",
                "plan_task" => "Plane Aufgabe",
                "request_folder_access" => "Fragt Ordnerzugriff an",
                "list_files" => "Liste Dateien auf",
                "search_files" => "Suche Dateien",
                "read_file" => "Lese Datei",
                "create_folder" => "Bereite neuen Ordner vor",
                "write_file" => "Bereite Dateiänderung vor",
                _ => "Führe Werkzeug aus"
            };
            if (subject.Length > 100) subject = subject[..100] + "…";
            return string.IsNullOrWhiteSpace(subject) ? action + " …" : $"{action}: {subject}";
        }
        catch (JsonException) { return "Führe einen angeforderten Arbeitsschritt aus …"; }
    }

    private static string ToolDisplayName(string name) => name switch
    {
        "plan_task" => "Aufgabenplan",
        "google_search" => "Websuche",
        "spotify_control" => "Spotify-Steuerung",
        "apple_music_control" => "Apple Music",
        "request_folder_access" => "Ordnerzugriff",
        "open_url" => "Open Link",
        "open_app" => "Open App",
        "list_files" => "List Files",
        "search_files" => "Search Files",
        "read_file" => "Read File",
        "create_folder" => "Create Folder",
        "write_file" => "Write File",
        _ => name.Replace('_', ' ')
    };

    private Button CreateMessageAction(string glyph, string tooltip, RoutedEventHandler handler, ChatRecord message)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, Tag = message, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2), FontSize = 11, Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.Transparent };
        button.Click += handler;
        return button;
    }

    private async void TtsMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ChatRecord message) return;
        try
        {
            if (ReferenceEquals(_activeTtsButton, button) && _tts.State == LocalTtsState.Speaking)
            {
                _tts.Pause(); button.Content = "▶"; button.ToolTip = "Vorlesen fortsetzen"; return;
            }
            if (ReferenceEquals(_activeTtsButton, button) && _tts.State == LocalTtsState.Paused)
            {
                _tts.Resume(); button.Content = "Ⅱ"; button.ToolTip = "Vorlesen pausieren · Stop mit erneutem Klick"; return;
            }
            if (_activeTtsButton is not null) ResetTtsButton();
            _activeTtsButton = button; _activeTtsStopButton = (button.Parent as Panel)?.Children.OfType<Button>().LastOrDefault();
            if (_activeTtsStopButton is not null) _activeTtsStopButton.Visibility = Visibility.Visible;
            button.Content = "Ⅱ"; button.ToolTip = "Vorlesen pausieren";
            await _tts.SpeakAsync(message.Text, _config.UiLanguage);
            Log("text-to-speech", true, $"Lokale Sprachausgabe gestartet ({message.Text.Length} Zeichen)");
        }
        catch (Exception ex)
        {
            ResetTtsButton(); Log("text-to-speech", false, ex.ToString());
            AddMessage("system", _config.UiLanguage == "de"
                ? "Die lokale Sprachausgabe konnte nicht gestartet werden: " + ex.Message + " Prüfe, ob das ausgewählte Sprachmodell installiert ist oder eine passende Windows-Stimme verfügbar ist."
                : "Local speech playback could not start. Check that the selected voice model is installed or that a matching Windows voice is available. Details were written to the Jarvis log.");
        }
    }

    private void ResetTtsButton()
    {
        if (_activeTtsButton is null) return;
        _activeTtsButton.Content = "◖))"; _activeTtsButton.ToolTip = "Antwort lokal vorlesen"; _activeTtsButton = null;
        if (_activeTtsStopButton is not null) _activeTtsStopButton.Visibility = Visibility.Collapsed;
        _activeTtsStopButton = null;
    }

    private void TtsStop_Click(object sender, RoutedEventArgs e) { _tts.Stop(); ResetTtsButton(); }
    private void UpdateTtsControls()
    {
        if (_activeTtsButton is null) return;
        if (_tts.State == LocalTtsState.Generating) { _activeTtsButton.Content = "…"; _activeTtsButton.ToolTip = _config.UiLanguage == "de" ? "Sprache wird erzeugt …" : "Generating speech…"; }
        else if (_tts.State == LocalTtsState.Paused) { _activeTtsButton.Content = "▶"; _activeTtsButton.ToolTip = _config.UiLanguage == "de" ? "Vorlesen fortsetzen" : "Resume speech"; }
        else if (_tts.State == LocalTtsState.Speaking) { _activeTtsButton.Content = "Ⅱ"; _activeTtsButton.ToolTip = _config.UiLanguage == "de" ? "Vorlesen pausieren" : "Pause speech"; }
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatRecord message }) return;
        try { System.Windows.Clipboard.SetText(message.Text); StatusText.Text = "Antwort kopiert"; }
        catch (Exception ex) { StatusText.Text = "Kopieren fehlgeschlagen: " + ex.Message; }
    }

    private void ShowLogs_Click(object sender, RoutedEventArgs e) => ShowLogsWindow();

    private void ShowLogsWindow()
    {
        string logText;
        try { logText = File.Exists(LogPath) ? string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(800)) : "Noch keine Jarvis-Protokolle vorhanden."; }
        catch (Exception ex) { logText = $"Das Protokoll konnte nicht gelesen werden: {ex.Message}"; }
        var viewer = new TextBox { Text = logText, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 12, Background = (Brush)FindResource("Field"), Foreground = (Brush)FindResource("Text"), BorderThickness = new Thickness(0), Padding = new Thickness(12) };
        var shell = new DockPanel { LastChildFill = true, Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var refresh = new Button { Content = "Aktualisieren", Margin = new Thickness(3) };
        var copy = new Button { Content = "Kopieren", Margin = new Thickness(3) };
        refresh.Click += (_, _) => { try { viewer.Text = File.Exists(LogPath) ? string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(800)) : "Noch keine Jarvis-Protokolle vorhanden."; } catch (Exception ex) { viewer.Text = "Protokoll konnte nicht gelesen werden: " + ex.Message; } };
        copy.Click += (_, _) => { try { System.Windows.Clipboard.SetText(viewer.Text); } catch { } };
        buttons.Children.Add(refresh); buttons.Children.Add(copy); DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons); shell.Children.Add(viewer);
        var window = new Window { Title = "Jarvis · Logs", Owner = this, Width = 920, Height = 560, MinWidth = 620, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("Bg"), Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI"), Content = shell };
        window.ShowDialog();
    }

    private async void RegenerateMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatRecord message } || _request is not null) return;
        var panels = Messages.Children.OfType<StackPanel>().ToList();
        var answerIndex = panels.FindIndex(panel => ReferenceEquals(panel.Tag, message));
        var userIndex = answerIndex - 1;
        while (userIndex >= 0 && (panels[userIndex].Tag as ChatRecord)?.Role != "user") userIndex--;
        if (answerIndex < 0 || userIndex < 0) { StatusText.Text = "Die zugehörige Nutzernachricht ist nicht mehr verfügbar."; return; }
        var previousUser = (ChatRecord)panels[userIndex].Tag;
        for (var i = Messages.Children.Count - 1; i >= userIndex; i--) Messages.Children.RemoveAt(i);
        if (_activeChat is not null) _activeChat.Messages = ReadChat();
        _pendingAttachments.Clear();
        if (previousUser.Attachments is not null) _pendingAttachments.AddRange(previousUser.Attachments);
        RefreshAttachmentSummary();
        PromptBox.Text = previousUser.Text;
        await SaveChat();
        await SendMessage();
    }
    private void RenderMarkdown(System.Windows.Controls.Panel target, string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n'); var code = new StringBuilder(); string? language = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (language is not null)
            {
                if (line.StartsWith("```")) { AddCodeBlock(target, code.ToString(), language); code.Clear(); language = null; }
                else code.AppendLine(line);
                continue;
            }
            if (line.StartsWith("```")) { language = line[3..].Trim(); continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#")) { var level = line.TakeWhile(c => c == '#').Count(); var heading = line[level..].Trim(); var headingBlock = InlineText(heading); headingBlock.FontSize = Math.Max(16, 25 - level * 2); headingBlock.FontWeight = FontWeights.SemiBold; headingBlock.Margin = new Thickness(0, 8, 0, 5); target.Children.Add(headingBlock); continue; }
            if (line.Contains('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$"))
            {
                var rows = new List<string[]> { SplitTableRow(line) }; i += 2;
                while (i < lines.Length && lines[i].Contains('|')) rows.Add(SplitTableRow(lines[i++]));
                i--; AddMarkdownTable(target, rows); continue;
            }            var bullet = Regex.Match(line, @"^\s*(?:[-*+]\s+|(\d+)\.\s+)(.*)$");
            if (bullet.Success) { var label = bullet.Groups[1].Success ? bullet.Groups[1].Value + ".  " : "•  "; target.Children.Add(InlineText(label + bullet.Groups[2].Value)); continue; }
            if (line.StartsWith(">")) { var quote = new Border { BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 3, 3, 3), Margin = new Thickness(0, 4, 0, 4), Child = InlineText(line[1..].Trim()) }; target.Children.Add(quote); continue; }
            if (Regex.IsMatch(line, @"^\s*([-*_])\1\1+\s*$")) { target.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(55, 56, 70)), Margin = new Thickness(0, 10, 0, 10) }); continue; }
            target.Children.Add(InlineText(line));
        }
        if (language is not null) AddCodeBlock(target, code.ToString(), language);
    }
    private static string[] SplitTableRow(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
    private void AddMarkdownTable(System.Windows.Controls.Panel target, List<string[]> rows)
    {
        var columns = Math.Max(1, rows.Max(row => row.Length)); var grid = new System.Windows.Controls.Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns; c++)
            {
                var value = c < rows[r].Length ? rows[r][c] : "";
                var cell = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(57, 58, 73)), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8), Background = r == 0 ? new SolidColorBrush(Color.FromRgb(39, 38, 56)) : System.Windows.Media.Brushes.Transparent, Child = InlineText(value) };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
            }
        }
        target.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 6, 0, 10) });
    }
    private System.Windows.Controls.RichTextBox InlineText(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 3), LineHeight = 22, Foreground = (Brush)FindResource("Text") };
        var block = new System.Windows.Controls.RichTextBox { IsReadOnly = true, IsDocumentEnabled = true, IsReadOnlyCaretVisible = false, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = 14, Foreground = (Brush)FindResource("Text"), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) }, Margin = new Thickness(0), Focusable = true };
        var matches = Regex.Matches(text, @"(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*|\[[^\]]+\]\(https?://[^)\s]+\))"); var position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position) paragraph.Inlines.Add(new Run(text[position..match.Index]));
            var raw = match.Value;
            if (raw.StartsWith("["))
            {
                var link = Regex.Match(raw, @"^\[([^\]]+)\]\((https?://[^)\s]+)\)$");
                if (link.Success && Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    var hyperlink = new Hyperlink(new Run(link.Groups[1].Value)) { NavigateUri = uri, Foreground = (Brush)FindResource("Accent") };
                    hyperlink.RequestNavigate += (_, args) => { try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { ToolError("Link öffnen", ex); } };
                    paragraph.Inlines.Add(hyperlink);
                }
                else paragraph.Inlines.Add(new Run(raw));
            }
            else if (raw.StartsWith('`')) paragraph.Inlines.Add(new Run(raw[1..^1]) { FontFamily = new FontFamily("Cascadia Code, Consolas"), Background = new SolidColorBrush(Color.FromRgb(39, 40, 52)), Foreground = new SolidColorBrush(Color.FromRgb(203, 190, 255)) });
            else if (raw.StartsWith("**")) paragraph.Inlines.Add(new Run(raw[2..^2]) { FontWeight = FontWeights.Bold });
            else paragraph.Inlines.Add(new Run(raw[1..^1]) { FontStyle = FontStyles.Italic });
            position = match.Index + match.Length;
        }
        if (position < text.Length) paragraph.Inlines.Add(new Run(text[position..]));
        return block;
    }
    private void AddCodeBlock(System.Windows.Controls.Panel target, string code, string language)
    {
        var top = new DockPanel { Margin = new Thickness(8, 5, 8, 0) }; top.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(language) ? "Code" : language, Foreground = (Brush)FindResource("Muted"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Content = "Kopieren", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += (_, _) => { System.Windows.Clipboard.SetText(code); StatusText.Text = "Codeblock kopiert"; }; DockPanel.SetDock(copy, Dock.Right); top.Children.Add(copy); target.Children.Add(top);
        var text = new TextBox { Text = code.TrimEnd(), IsReadOnly = true, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 12, Background = new SolidColorBrush(Color.FromRgb(17, 18, 24)), BorderThickness = new Thickness(0), Padding = new Thickness(10), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, MaxHeight = 320, MinHeight = Math.Min(100, Math.Max(40, (code.Count(c => c == '\n') + 1) * 18)) };
        target.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(17, 18, 24)), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 9), Child = text });
    }
    private async Task SaveChat()
    {
        if (_activeChat is null) return;
        _activeChat.Messages = ReadChat(); _activeChat.UpdatedAt = DateTimeOffset.Now;
        var firstUserMessage = _activeChat.Messages.FirstOrDefault(m => m.Role == "user")?.Text;
        if (_activeChat.Title == "Neuer Chat" && !string.IsNullOrWhiteSpace(firstUserMessage)) _activeChat.Title = UserTitle(firstUserMessage);
        ChatList.Items.Refresh(); ChatTabs.Items.Refresh();
        try { await PersistChatState(); }
        catch (Exception ex) { Log("chat-storage", false, ex.Message); StatusText.Text = "Chatverlauf konnte nicht gespeichert werden: " + ex.Message; }
    }
    private static string UserTitle(string text)
    {
        var compact = Regex.Replace(text, @"\s+", " ").Trim();
        return compact.Length > 34 ? compact[..34].TrimEnd() + "…" : compact;
    }

    private async Task GenerateChatTitleAsync(string model, string endpoint, CancellationToken cancellationToken)
    {
        if (_activeChat is null) return;
        var conversation = ReadChat();
        var firstUser = conversation.FirstOrDefault(item => item.Role == "user")?.Text;
        var lastAssistant = conversation.LastOrDefault(item => item.Role == "assistant")?.Text;
        if (string.IsNullOrWhiteSpace(firstUser) || string.IsNullOrWhiteSpace(lastAssistant) || _activeChat.Title != UserTitle(firstUser)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var prompt = $"Erstelle einen kurzen, präzisen Titel (2 bis 6 Wörter) für diesen Chat. Gib nur den Titel aus, ohne Anführungszeichen oder Erklärung.\nErste Nutzernachricht: {firstUser[..Math.Min(firstUser.Length, 500)]}\nAntwort: {lastAssistant[..Math.Min(lastAssistant.Length, 700)]}";
            var payload = new { model, messages = new[] { new { role = "system", content = "Du benennst Chats knapp und konkret in der Sprache des Nutzers." }, new { role = "user", content = prompt } }, temperature = 0.2, max_tokens = 24 };
            using var response = await _http.PostAsync(EndpointUri("chat/completions", endpoint), new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Trim(body)}");
            using var json = JsonDocument.Parse(body);
            var title = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(title)) return;
            title = Regex.Replace(title.Trim().Trim('"', '\'', '`'), @"\s+", " ");
            if (title.Length > 48) title = title[..48].TrimEnd();
            if (title.Length < 2 || title.Contains('\n')) return;
            _activeChat.Title = title;
            ChatTabs.Items.Refresh(); ChatList.Items.Refresh();
            await SaveChat();
        }
        catch (Exception ex) { Log("chat-title", false, ex.Message); }
    }
    private async Task PersistChatState()
    {
        if (_loading) return;
        _config.ActiveChatId = _activeChat?.Id ?? "";
        _config.OpenTabIds = _openTabs.Select(chat => chat.Id).ToList();
        await PersistConfig();
        if (_config.SaveChat) await _chatStore.SaveAsync(ChatsPath, _chats.ToArray());
    }
    private List<ChatRecord> ReadChat() => Messages.Children.OfType<StackPanel>().Select(p => p.Tag as ChatRecord).Where(m => m is not null).Select(m => m!).ToList();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _request?.Cancel();

    private async void ChatModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ChatModelBox is null || string.IsNullOrWhiteSpace(SelectedChatModel())) return;
        var model = SelectedChatModel(); if (_activeChat is not null) _activeChat.Model = model;
        _config.Model = model; ModelBox.Text = model; ModelStatus.Text = "Ausgewählt: " + model;
        try { await PersistChatState(); } catch (Exception ex) { SettingsStatus.Text = "Modell konnte nicht gespeichert werden: " + ex.Message; }
    }
    private async void ChatModel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || string.IsNullOrWhiteSpace(SelectedChatModel())) return;
        _config.Model = SelectedChatModel(); ModelBox.Text = _config.Model; if (_activeChat is not null) _activeChat.Model = _config.Model;
        try { await PersistChatState(); } catch (Exception ex) { StatusText.Text = "Modellwahl konnte nicht gespeichert werden: " + ex.Message; }
    }
    private async void ModelPrompt_Click(object sender, RoutedEventArgs e)
    {
        var selected = PromptForText("Modell auswählen", "Modell-ID des OpenAI-kompatiblen Servers", SelectedChatModel());
        if (string.IsNullOrWhiteSpace(selected)) return;
        var model = selected.Trim(); _config.Model = model; ModelBox.Text = model; if (_activeChat is not null) _activeChat.Model = model;
        SetChatModel(model);
        try { await PersistChatState(); ModelStatus.Text = "Modell: " + model; }
        catch (Exception ex) { StatusText.Text = "Modellwahl konnte nicht gespeichert werden: " + ex.Message; }
    }
    private void ResetPrompt_Click(object sender, RoutedEventArgs e) { SystemPromptBox.Text = DefaultPrompt; _config.SystemPrompt = DefaultPrompt; SettingsStatus.Text = "Standardprompt geladen. Zum Übernehmen speichern."; }
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig();
        if (sender is Button button) button.IsEnabled = false;
        var testing = _config.UiLanguage == "de" ? "Verbindung wird geprüft …" : "Testing connection…";
        SettingsStatus.Text = testing; StatusText.Text = testing;
        try { await PersistConfig(); await TestEndpoint(true); StatusText.Text = SettingsStatus.Text; }
        catch (Exception ex) { SettingsStatus.Text = (_config.UiLanguage == "de" ? "Einstellungen konnten nicht gespeichert werden: " : "Could not save settings: ") + ex.Message; StatusText.Text = SettingsStatus.Text; Log("connection-test", false, ex.Message); }
        finally { if (sender is Button testButton) testButton.IsEnabled = true; }
    }
    private async Task TestEndpoint(bool showStatus)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await _http.GetAsync(EndpointUri("models"), cts.Token); var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Trim(body)}");
            ConnectionBadge.Text = (_config.UiLanguage == "de" ? "Verbunden · " : "Connected · ") + new Uri(_config.Endpoint).Host; if (showStatus) SettingsStatus.Text = _config.UiLanguage == "de" ? "Verbindung erfolgreich. /models antwortet." : "Connection successful. /models responded."; Log("connection-test", true, "Models endpoint erreichbar");
        }
        catch (Exception ex) { ConnectionBadge.Text = _config.UiLanguage == "de" ? "Server nicht erreichbar · lokal" : "Server unavailable · local"; if (showStatus) SettingsStatus.Text = FriendlyEndpointError(_config.Endpoint, ex); Log("connection-test", false, ex.Message); }
    }
    private async void Models_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig(); await RefreshModelsAsync(true);
    }
    private async Task RefreshModelsAsync(bool showDetails)
    {
        if (showDetails) ModelsResult.Text = _config.UiLanguage == "de" ? "Modelle werden geladen …" : "Loading models…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var (models, canLoad, source) = await DiscoverModelsAsync(cts.Token);
            _availableModels = models;
            _modelsSource = source;
            _modelDiscoveryFailed = false;
            _modelDiscoveryError = "";
            _ = canLoad;
            var current = SelectedChatModel();
            var selected = models.Any(option => string.Equals(option.Id, current, StringComparison.OrdinalIgnoreCase))
                ? current
                : models.FirstOrDefault()?.Id ?? current;
            var options = models.ToList();
            if (!string.IsNullOrWhiteSpace(selected) && !options.Any(option => string.Equals(option.Id, selected, StringComparison.OrdinalIgnoreCase)))
                options.Insert(0, new ChatModelOption(selected, selected + " · nicht im Serverkatalog", false, false));
            var wasLoading = _loading; _loading = true;
            ChatModelBox.ItemsSource = options;
            ChatModelBox.SelectedValue = string.IsNullOrWhiteSpace(selected) ? null : selected;
            if (!string.IsNullOrWhiteSpace(selected) && !string.Equals(selected, _config.Model, StringComparison.Ordinal))
            {
                _config.Model = selected;
                ModelBox.Text = selected;
                if (_activeChat is not null) _activeChat.Model = selected;
            }
            _loading = wasLoading;
            ConnectionBadge.Text = (_config.UiLanguage == "de" ? "Verbunden · " : "Connected · ") + new Uri(_config.Endpoint).Host;
            var loadedCount = models.Count(option => option.IsLoaded);
            ModelStatus.Text = models.Count == 0 ? (_config.UiLanguage == "de" ? "Server erreichbar · keine Chatmodelle gefunden" : "Server reachable · no chat models found") : _config.UiLanguage == "de" ? $"{models.Count} Chatmodell(e) · {loadedCount} geladen · andere laden beim Senden" : $"{models.Count} chat model(s) · {loadedCount} loaded · others load when sent";
            if (showDetails) ModelsResult.Text = models.Count == 0 ? (_config.UiLanguage == "de" ? "Der Server antwortet, liefert aber keine Chatmodelle." : "The server responded but did not provide any chat models.") : _config.UiLanguage == "de" ? $"{models.Count} Chatmodell(e) aus {source}. Geladene: {loadedCount}; weitere Modelle werden beim Senden geladen, wenn der Server das unterstützt." : $"{models.Count} chat model(s) from {source}. Loaded: {loadedCount}; other models will load when sent if supported by the server.";
            if (models.Count > 0) { try { await PersistConfig(); await PersistChatState(); } catch (Exception ex) { Log("model-discovery-save", false, ex.Message); } }
            Log("model-discovery", true, $"{models.Count} Chatmodell(e) erkannt aus {source}");
        }
        catch (Exception ex)
        {
            _modelDiscoveryFailed = true;
            _modelDiscoveryError = FriendlyEndpointError(_config.Endpoint, ex);
            ConnectionBadge.Text = _config.UiLanguage == "de" ? "Server nicht erreichbar · lokal" : "Server unavailable · local";
            ModelStatus.Text = _config.UiLanguage == "de" ? "Kein Server verbunden · Modell manuell wählbar" : "No server connected · model can be entered manually";
            if (showDetails) ModelsResult.Text = _modelDiscoveryError + (_config.UiLanguage == "de" ? "\nModellnamen kannst du weiterhin manuell eintragen." : "\nYou can still enter a model name manually.");
            Log("model-discovery", false, ex.Message);
        }
    }
    private async Task<(List<ChatModelOption> Models, bool CanLoad, string Source)> DiscoverModelsAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var localApi = new UriBuilder(_config.Endpoint) { Path = "/api/v1/models", Query = "", Fragment = "" }.Uri;
        if (localApi.IsLoopback)
        {
            try
            {
                using var response = await _http.GetAsync(localApi, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("models", out var allModels) && allModels.ValueKind == JsonValueKind.Array)
                    {
                        var options = allModels.EnumerateArray()
                            .Where(item => !item.TryGetProperty("type", out var type) || type.GetString() is "llm" or "decision")
                            .Where(item => item.TryGetProperty("key", out _))
                            .Select(item =>
                            {
                                var id = item.GetProperty("key").GetString() ?? "";
                                var name = item.TryGetProperty("display_name", out var display) ? display.GetString() : null;
                                var loaded = item.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0;
                                return new ChatModelOption(id, $"{(string.IsNullOrWhiteSpace(name) ? id : name)} · {(loaded ? "geladen" : "bei Bedarf laden")}", loaded, true);
                            })
                            .Where(option => option.Id.Length > 0)
                            .DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (options.Count > 0) return (options, true, "LM Studio (/api/v1/models)");
                        failures.Add("LM Studio /api/v1/models: keine lokalen Modelle aufgeführt");
                    }
                    failures.Add("LM Studio /api/v1/models: ungültige Antwort");
                }
                else failures.Add($"LM Studio /api/v1/models: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { failures.Add("LM Studio API v1: " + ex.Message); }

            try
            {
                using var response = await _http.GetAsync(new UriBuilder(_config.Endpoint) { Path = "/api/v0/models", Query = "", Fragment = "" }.Uri, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        var options = data.EnumerateArray().Where(item => item.TryGetProperty("id", out _))
                            .Where(item => !item.TryGetProperty("type", out var type) || type.GetString() is "llm" or "llm (gguf)" or "llm (mlx)" or "vlm")
                            .Select(item => { var id = item.GetProperty("id").GetString() ?? ""; var stateText = item.TryGetProperty("state", out var state) ? state.GetString() : null; var loaded = string.Equals(stateText, "loaded", StringComparison.OrdinalIgnoreCase) || string.Equals(stateText, "loaded ( idle )", StringComparison.OrdinalIgnoreCase) || stateText?.StartsWith("loaded ", StringComparison.OrdinalIgnoreCase) == true; return new ChatModelOption(id, $"{id} · {(loaded ? "geladen" : "bei Bedarf laden")}", loaded, true); })
                            .Where(option => option.Id.Length > 0).DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToList();
                        if (options.Count > 0) return (options, true, "LM Studio (/api/v0/models)");
                        failures.Add("LM Studio /api/v0/models: keine lokalen Modelle aufgeführt");
                    }
                }
                else failures.Add($"LM Studio /api/v0/models: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { failures.Add("LM Studio API v0: " + ex.Message); }
        }

        if (localApi.IsLoopback)
        {
            try
            {
                var catalog = await ReadLmStudioCliCatalogAsync(cancellationToken);
                if (catalog.Count > 0) return (catalog, true, "LM Studio CLI (vollständiger lokaler Katalog)");
                failures.Add("LM Studio CLI: keine heruntergeladenen Modelle gefunden");
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or JsonException or Win32Exception or IOException)
            {
                failures.Add("LM Studio CLI: " + ex.Message);
            }
        }

        using (var response = await _http.GetAsync(EndpointUri("models"), cancellationToken))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"/models meldet HTTP {(int)response.StatusCode}: {Trim(body)}. " + string.Join("; ", failures));
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Die Antwort von /models enthält kein gültiges 'data'-Array.");
            var models = data.EnumerateArray().Where(item => item.TryGetProperty("id", out _)).Select(item => item.GetProperty("id").GetString()).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => new ChatModelOption(id!, id!, true, false)).DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToList();
            return (models, false, "OpenAI-kompatiblem /models");
        }
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig(); try { await PersistConfig(); SettingsStatus.Text = "Einstellungen gespeichert."; await RefreshModelsAsync(false); }
        catch (Exception ex) { SettingsStatus.Text = "Speichern fehlgeschlagen: " + ex.Message; Log("settings", false, ex.Message); }
    }

    private void RefreshSpeechDevices(bool updateStatus, IReadOnlyList<SpeechDevice>? detectedDevices = null)
    {
        if (SpeechDeviceBox is null || _speechBusy) return;
        try
        {
            var previousId = _config.SpeechDeviceId;
            var devices = detectedDevices ?? _speech.GetDevices();
            SpeechDeviceBox.ItemsSource = devices;
            SpeechDeviceBox.SelectedValue = previousId;
            SpeechDeviceBox.IsEnabled = SpeechUseDefaultCheck.IsChecked != true;
            if (devices.Count == 0)
            {
                SpeechDeviceStatus.Text = _config.UiLanguage == "de" ? "Kein aktives Mikrofon gefunden. Prüfe Windows-Datenschutzeinstellungen für den Mikrofonzugriff." : "No active microphone found. Check Windows microphone privacy access.";
                if (updateStatus) SpeechModelStatus.Text = _config.UiLanguage == "de" ? "Mikrofon nicht verfügbar." : "Microphone unavailable.";
                MicButton.IsEnabled = false;
                return;
            }
            MicButton.IsEnabled = SpeechEnabledCheck.IsChecked == true;
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
            var selected = SpeechUseDefaultCheck.IsChecked == true ? defaultDevice : devices.FirstOrDefault(d => d.Id == previousId);
            if (SpeechUseDefaultCheck.IsChecked != true && selected is null)
            {
                SpeechDeviceStatus.Text = _config.UiLanguage == "de" ? "Das gespeicherte Mikrofon ist nicht verfügbar. Jarvis wechselt nicht automatisch zu einem anderen Gerät." : "The saved microphone is unavailable. Jarvis will not switch to another device automatically.";
                MicButton.IsEnabled = false;
            }
            else SpeechDeviceStatus.Text = _config.UiLanguage == "de" ? $"Aktuell verwendet: {selected?.Name ?? "Windows-Standardgerät nicht verfügbar"}" : $"Currently using: {selected?.Name ?? "Windows default device unavailable"}";
        }
        catch (Exception ex)
        {
            MicButton.IsEnabled = false;
            SpeechDeviceStatus.Text = (_config.UiLanguage == "de" ? "Mikrofonzugriff nicht möglich: " : "Microphone access failed: ") + (_config.UiLanguage == "de" ? ex.Message : "Check Windows microphone privacy settings.");
        }
    }

    private async void SpeechDevices_RefreshClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        SpeechDeviceStatus.Text = _config.UiLanguage == "de" ? "Suche verfügbare Windows-Aufnahmegeräte …" : "Scanning Windows recording devices…";
        try
        {
            var devices = await Task.Run(() => { using var enumerator = new LocalSpeechService(); return enumerator.GetDevices(); });
            RefreshSpeechDevices(true, devices);
            SpeechDeviceStatus.Text = devices.Count == 0
                ? (_config.UiLanguage == "de" ? "Keine aktiven Mikrofone gefunden. Windows-Datenschutzzugriff prüfen." : "No active microphones found. Check Windows microphone privacy access.")
                : (_config.UiLanguage == "de" ? $"{devices.Count} Mikrofon(e) gefunden. Aktuelle Auswahl: {SpeechDeviceStatus.Text}" : $"Found {devices.Count} microphone(s). Current selection: {SpeechDeviceStatus.Text}");
        }
        catch (Exception ex)
        {
            SpeechDeviceStatus.Text = (_config.UiLanguage == "de" ? "Mikrofone konnten nicht aufgelistet werden: " : "Could not enumerate microphones: ") + ex.Message;
            Log("speech-device-discovery", false, ex.ToString());
        }
        finally { if (sender is Button refreshButton) refreshButton.IsEnabled = true; }
    }
    private void SpeechEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (MicButton is null || SpeechEnabledCheck is null) return;
        MicButton.IsEnabled = SpeechEnabledCheck.IsChecked == true && SpeechDeviceBox?.Items.Count > 0;
        if (SpeechEnabledCheck.IsChecked != true && WakeWordEnabledCheck?.IsChecked == true) WakeWordEnabledCheck.IsChecked = false;
    }
    private void SpeechDefault_Changed(object sender, RoutedEventArgs e)
    {
        if (SpeechDeviceBox is null) return;
        SpeechDeviceBox.IsEnabled = SpeechUseDefaultCheck.IsChecked != true;
        RefreshSpeechDevices(false);
    }

    private void SpeechModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeechModelBox is null || SpeechModelStatus is null) return;
        UpdateSpeechModelStatus();
    }

    private string SelectedSpeechModel => SpeechModelBox?.SelectedValue?.ToString() is { Length: > 0 } model ? model : "base";
    private static string SpeechModelFileName(string size) => size switch
    {
        "large-v3-turbo" => "ggml-large-v3-turbo-q5_1.bin",
        _ => $"ggml-{size}-q5_1.bin"
    };
    private string SpeechModelPath(string? size = null) => Path.Combine(SpeechModelsPath, SpeechModelFileName(size ?? SelectedSpeechModel));

    private void UpdateSpeechModelStatus()
    {
        var german = _config.UiLanguage == "de";
        var path = SpeechModelPath();
        SpeechModelStatus.Text = File.Exists(path)
            ? german ? $"Bereit · lokal gespeichert ({new FileInfo(path).Length / 1024 / 1024} MB)" : $"Ready · stored locally ({new FileInfo(path).Length / 1024 / 1024} MB)"
            : SelectedSpeechModel == "large-v3-turbo"
                ? german ? "Nicht installiert · hoher Speicherbedarf; nach dem Download offline nutzbar." : "Not installed · requires more storage; available offline after download."
                : german ? "Nicht installiert · beim ersten Mal herunterladen; danach offline nutzbar." : "Not installed · download once; then available offline.";
        SpeechDownloadButton.Content = File.Exists(path) ? (german ? "Modell erneut herunterladen" : "Download model again") : (german ? "Modell herunterladen" : "Download model");
    }

    private async void SpeechSave_Click(object sender, RoutedEventArgs e)
    {
        _config.SpeechEnabled = SpeechEnabledCheck.IsChecked == true;
        _config.SpeechUseWindowsDefault = SpeechUseDefaultCheck.IsChecked == true;
        _config.SpeechDeviceId = SpeechDeviceBox.SelectedValue?.ToString() ?? "";
        _config.SpeechModel = SelectedSpeechModel;
        _config.WakeWordEnabled = WakeWordEnabledCheck.IsChecked == true;
        _config.WakeWordPhrase = string.IsNullOrWhiteSpace(WakeWordPhraseBox.Text) ? "Hey Jarvis" : WakeWordPhraseBox.Text.Trim();
        _config.WakeSoundVolume = Math.Clamp(WakeSoundVolumeSlider.Value / 100d, 0d, 1d);
        _config.VoiceEndSilenceMs = (int)Math.Clamp(VoiceEndSilenceSlider.Value, 350d, 1500d);
        _config.TtsVoiceName = TtsVoiceBox.SelectedValue?.ToString() ?? "";
        _config.TtsPiperVoiceId = TtsPiperVoiceBox.SelectedValue?.ToString() ?? "de_DE-thorsten-high";
        _config.TtsVolume = Math.Clamp(TtsVolumeSlider.Value / 100d, 0d, 1d);
        _config.TtsEngine = TtsEngineBox.SelectedValue?.ToString() ?? "windows"; _config.ResourceDirectory = ResourceDirectory;
        _tts.PreferredVoiceName = _config.TtsVoiceName; _tts.PiperVoiceId = _config.TtsPiperVoiceId; _tts.Volume = (float)_config.TtsVolume; _tts.Engine = _config.TtsEngine; _tts.ResourceDirectory = ResourceDirectory;
        try { await PersistConfig(); SpeechDeviceStatus.Text = _config.UiLanguage == "de" ? "Spracheinstellungen gespeichert." : "Speech settings saved."; if (TtsVoiceStatus is not null) TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Stimm-Einstellungen gespeichert." : "Voice settings saved."; RefreshSpeechDevices(false); await StopWakeWordMonitoringAsync(); if (_config.WakeWordEnabled) StartWakeWordMonitoring(); }
        catch (Exception ex) { SpeechDeviceStatus.Text = (_config.UiLanguage == "de" ? "Speichern fehlgeschlagen: " : "Could not save: ") + ex.Message; }
    }

    private async void TtsPreview_Click(object sender, RoutedEventArgs e)
    {
        _tts.PreferredVoiceName = TtsVoiceBox.SelectedValue?.ToString() ?? "";
        var language = TtsEngineBox.SelectedValue?.ToString() == "piper"
            ? (_tts.PiperVoiceLanguage == "en" ? "en-US" : "de-DE")
            : (TtsVoiceBox.SelectedItem as InstalledVoice)?.Culture ?? _config.UiLanguage;
        TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Stimmprobe wird lokal erzeugt …" : "Generating a local voice preview…";
        try
        {
            await _tts.SpeakAsync(language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
                ? "Hallo, ich bin Jarvis. Ich spreche diese kurze Probe mit der ausgewählten Stimme."
                : "Hello, I am Jarvis. This short preview uses the selected voice.", language);
            TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Stimmprobe abgeschlossen." : "Voice preview finished.";
        }
        catch (Exception ex)
        {
            TtsVoiceStatus.Text = (_config.UiLanguage == "de" ? "Stimmprobe fehlgeschlagen: " : "Voice preview failed: ") + ex.Message;
            Log("text-to-speech-preview", false, ex.ToString());
        }
    }

    private void TtsEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TtsEngineBox?.SelectedValue is not string engine) return;
        _tts.Engine = engine;
        RefreshTtsModelStatus();
    }

    private void TtsPiperVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TtsPiperVoiceBox?.SelectedValue is not string voiceId) return;
        _tts.PiperVoiceId = voiceId;
        if (!_loading && TtsEngineBox.SelectedValue?.ToString() != "piper") TtsEngineBox.SelectedValue = "piper";
        RefreshTtsModelStatus();
    }

    private void TtsVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_tts is not null) _tts.Volume = (float)(e.NewValue / 100d);
        if (TtsVolumeValue is not null) TtsVolumeValue.Text = $"{Math.Round(e.NewValue)} %";
    }

    private void WakeSoundVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _config.WakeSoundVolume = Math.Clamp(e.NewValue / 100d, 0d, 1d);
        if (WakeSoundVolumeValue is not null) WakeSoundVolumeValue.Text = $"{Math.Round(e.NewValue)} %";
    }

    private void VoiceEndSilence_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VoiceEndSilenceValue is not null) VoiceEndSilenceValue.Text = $"{Math.Round(e.NewValue)} ms";
        if (!_loading) _config.VoiceEndSilenceMs = (int)Math.Clamp(e.NewValue, 350d, 1500d);
    }

    private void WakeSoundTest_Click(object sender, RoutedEventArgs e) => PlayWakeSound();

    private void PlayWakeSound()
    {
        if (_config.WakeSoundVolume <= 0) return;
        try
        {
            StopWakeSound();
            var soundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "wake-meme-click.mp3");
            if (!File.Exists(soundPath)) throw new FileNotFoundException("The configured wake sound is missing.", soundPath);
            var reader = new AudioFileReader(soundPath);
            var output = new WaveOutEvent { Volume = (float)Math.Clamp(_config.WakeSoundVolume, 0d, 1d) };
            output.PlaybackStopped += WakeSoundPlaybackStopped;
            _wakeSoundReader = reader;
            _wakeSoundOutput = output;
            output.Init(reader);
            output.Play();
        }
        catch (Exception ex)
        {
            StopWakeSound();
            Log("wake-word-sound", false, ex.ToString());
        }
    }

    private void WakeSoundPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WaveOutEvent output) return;
        if (e.Exception is not null) Log("wake-word-sound", false, e.Exception.ToString());
        _ = Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(_wakeSoundOutput, output)) StopWakeSound(); }));
    }

    private void StopWakeSound()
    {
        var output = _wakeSoundOutput;
        var reader = _wakeSoundReader;
        _wakeSoundOutput = null;
        _wakeSoundReader = null;
        if (output is not null)
        {
            output.PlaybackStopped -= WakeSoundPlaybackStopped;
            try { output.Stop(); } catch { }
            output.Dispose();
        }
        reader?.Dispose();
    }

    private void RefreshTtsModelStatus()
    {
        if (TtsModelStatus is null || TtsDownloadButton is null) return;
        var voiceId = TtsPiperVoiceBox?.SelectedValue?.ToString() ?? _config.TtsPiperVoiceId;
        var voice = LocalTtsService.PiperVoices.FirstOrDefault(v => v.Id == voiceId) ?? LocalTtsService.PiperVoices[0];
        var installed = _tts.IsPiperModelInstalled(ResourceDirectory, voice.Id);
        var isPiper = TtsEngineBox.SelectedValue?.ToString() == "piper";
        if (TtsPiperVoiceBox is not null) TtsPiperVoiceBox.IsEnabled = true;
        TtsDownloadButton.IsEnabled = !installed && _ttsDownloadOperation is null;
        TtsModelStatus.Text = installed
            ? (_config.UiLanguage == "de" ? $"{voice.Name} bereit · gespeichert unter: {Path.Combine(ResourceDirectory, "Models", "TTS")}" : $"{voice.Name} ready · stored at {Path.Combine(ResourceDirectory, "Models", "TTS")}")
            : (_config.UiLanguage == "de" ? $"Noch nicht heruntergeladen · ungefähr {voice.ApproximateMegabytes} MB. Danach offline nutzbar." : $"Not downloaded · approximately {voice.ApproximateMegabytes} MB. Works offline afterward.");
    }

    private async void TtsDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_ttsDownloadOperation is not null) return;
        _ttsDownloadOperation = new CancellationTokenSource();
        TtsDownloadProgress.Visibility = Visibility.Visible; TtsDownloadProgress.IsIndeterminate = true; TtsDownloadButton.IsEnabled = false;
        TtsModelStatus.Text = _config.UiLanguage == "de" ? "Lade das ausgewählte Neural-Sprachmodell vom offiziellen Sherpa-ONNX-Modellserver herunter …" : "Downloading the selected neural voice model from the official Sherpa-ONNX model host…";
        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(ResourceDirectory));
            if (string.IsNullOrWhiteSpace(volumeRoot) || new DriveInfo(volumeRoot).AvailableFreeSpace < 300L * 1024 * 1024)
                throw new IOException(_config.UiLanguage == "de" ? "Im gewählten Jarvis-Ordner sind weniger als 300 MB frei. Wähle im Setup einen Speicherort mit mehr freiem Speicher." : "The selected Jarvis folder has less than 300 MB free. Choose a location with more free space in Setup.");
            var voiceId = TtsPiperVoiceBox.SelectedValue?.ToString() ?? "de_DE-thorsten-high";
            var voice = LocalTtsService.PiperVoices.First(v => v.Id == voiceId);
            var progress = new Progress<long>(value => TtsModelStatus.Text = _config.UiLanguage == "de" ? $"Sprachmodell wird geladen · {value}% …" : $"Downloading voice model · {value}%…");
            await _tts.DownloadPiperModelAsync(ResourceDirectory, voice.Id, progress, _ttsDownloadOperation.Token);
            _config.ResourceDirectory = ResourceDirectory; _config.TtsPiperVoiceId = voice.Id; _config.TtsEngine = "piper"; _tts.PiperVoiceId = voice.Id; _tts.Engine = "piper"; TtsEngineBox.SelectedValue = "piper";
            await PersistConfig();
            TtsModelStatus.Text = _config.UiLanguage == "de" ? "Neural-Stimme installiert und aktiviert. Ab jetzt lokal und offline verfügbar." : "Neural voice installed and enabled. It is now available locally and offline.";
            Log("tts-model-download", true, $"Piper {voice.Id} downloaded and extracted into the configured Jarvis resource directory.");
        }
        catch (OperationCanceledException) { TtsModelStatus.Text = _config.UiLanguage == "de" ? "Download abgebrochen." : "Download cancelled."; }
        catch (Exception ex) { TtsModelStatus.Text = (_config.UiLanguage == "de" ? "Neural-Stimme konnte nicht installiert werden: " : "Could not install neural voice: ") + ex.Message; Log("tts-model-download", false, ex.ToString()); }
        finally { _ttsDownloadOperation.Dispose(); _ttsDownloadOperation = null; TtsDownloadProgress.Visibility = Visibility.Collapsed; RefreshTtsModelStatus(); }
    }

    private async void SpeechDownload_Click(object sender, RoutedEventArgs e)
    {
        SpeechDownloadButton.IsEnabled = false; SpeechDownloadProgress.Visibility = Visibility.Visible;
        SpeechModelStatus.Text = _config.UiLanguage == "de" ? "Whisper-Modell wird geladen. Der Download kann je nach Verbindung einige Minuten dauern." : "Downloading the Whisper model. This may take a few minutes depending on your connection.";
        try
        {
            var progress = new Progress<double>(bytes => SpeechModelStatus.Text = $"Whisper-Modell wird geladen · {bytes / 1024 / 1024} MB empfangen …");
            await LocalSpeechService.DownloadModelAsync(SelectedSpeechModel, SpeechModelPath(), CancellationToken.None, progress);
            UpdateSpeechModelStatus();
        }
        catch (Exception ex) { SpeechModelStatus.Text = (_config.UiLanguage == "de" ? "Download fehlgeschlagen: " : "Download failed: ") + ex.Message; Log("speech-model-download", false, ex.ToString()); }
        finally { SpeechDownloadProgress.Visibility = Visibility.Collapsed; SpeechDownloadButton.IsEnabled = true; }
    }

    private async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_speechBusy) return;
        if (_wakeWordListening) await StopWakeWordMonitoringAsync();
        if (SpeechEnabledCheck.IsChecked != true) { ShowSpeechSetupMessage("Spracheingabe ist deaktiviert. Aktiviere sie unter Einstellungen → Spracheingabe."); return; }
        if (!File.Exists(SpeechModelPath())) { ShowSpeechSetupMessage("Das lokale Whisper-Modell ist noch nicht installiert. Lade es einmalig unter Einstellungen → Spracheingabe herunter; danach funktioniert die Erkennung offline."); return; }
        RefreshSpeechDevices(false);
        if (SpeechUseDefaultCheck.IsChecked != true && SpeechDeviceBox.SelectedItem is not SpeechDevice) { ShowSpeechSetupMessage("Das ausgewählte Mikrofon ist nicht verfügbar. Wähle in Einstellungen → Spracheingabe ein aktives Gerät oder das Windows-Standardgerät."); return; }
        var path = Path.Combine(SpeechModelsPath, $"capture-{Guid.NewGuid():N}.wav");
        try
        {
            var selectedId = SpeechDeviceBox.SelectedValue?.ToString() ?? ""; _speechLastCapturePath = path; _speechTranscribing = false;
            _voiceSpeechDetected = false; _voiceStopScheduled = false; _voiceLastSpeechAt = DateTime.MinValue;
            var wakePrefill = _voiceModeEnabled && _wakeWordPrefillPending ? _wakeWordPrefillAudio : null;
            var wakePrefillFormat = _voiceModeEnabled && _wakeWordPrefillPending ? _wakeWordPrefillFormat : null;
            _wakeWordPrefillAudio = null; _wakeWordPrefillFormat = null; _wakeWordPrefillPending = false;
            _speech.StartRecording(selectedId, SpeechUseDefaultCheck.IsChecked == true, path, level => Dispatcher.BeginInvoke(() => OnSpeechLevel(level)), wakePrefill, wakePrefillFormat);
            _speechBusy = true; _speechOperation = new CancellationTokenSource(); _speechCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); SpeechStatusRow.Visibility = _voiceModeEnabled ? Visibility.Collapsed : Visibility.Visible;
            SpeechSettingsButton.Visibility = Visibility.Collapsed; SpeechStopButton.Visibility = Visibility.Visible; SpeechCancelButton.Visibility = Visibility.Visible; MicButton.IsEnabled = false;
            var device = SpeechUseDefaultCheck.IsChecked == true ? _speech.GetDefaultDeviceName() : (SpeechDeviceBox.SelectedItem as SpeechDevice)?.Name ?? (_config.UiLanguage == "de" ? "ausgewähltes Mikrofon" : "selected microphone");
            SpeechStatusText.Text = _config.UiLanguage == "de" ? $"Jarvis hört zu · {device}" : $"Jarvis is listening · {device}";
            StatusText.Text = _config.UiLanguage == "de" ? "Spracheingabe · Aufnahme läuft" : "Speech input · recording";
            _voiceWindow?.SetState(VoiceUiState.Listening, "Listening…", $"Microphone: {device}. Jarvis starts when you finish speaking.");
            Log("speech-recording", true, "Mikrofonaufnahme gestartet");
        }
        catch (Exception ex) { _speechBusy = false; ShowSpeechSetupMessage("Mikrofonaufnahme konnte nicht starten: " + ex.Message); Log("speech-recording", false, ex.ToString()); }
    }

    private void ShowSpeechSetupMessage(string text)
    {
        text = Jarvis.Services.UiLocalization.Translate(text, _config.UiLanguage);
        if (_voiceModeEnabled) { _voiceWindow?.SetState(VoiceUiState.Error, "Microphone or speech model unavailable", text); SpeechStatusRow.Visibility = Visibility.Collapsed; }
        else { SpeechStatusRow.Visibility = Visibility.Visible; SpeechStatusText.Text = text; SpeechStopButton.Visibility = Visibility.Collapsed; SpeechCancelButton.Visibility = Visibility.Collapsed; SpeechMeter.Visibility = Visibility.Collapsed; SpeechSettingsButton.Visibility = Visibility.Visible; }
        Log("speech-input", false, text);
    }
    private void SpeechSettings_Click(object sender, RoutedEventArgs e)
    {
        SpeechStatusRow.Visibility = Visibility.Collapsed; SpeechMeter.Visibility = Visibility.Visible; NavigateTo("Einstellungen"); SettingsTabs.SelectedIndex = 3;
    }

    private async void SpeechStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_speechBusy) return;
        var operation = _speechOperation;
        var completion = _speechCompletion;
        var capturePath = _speechLastCapturePath;
        var handedOffToVoiceReply = false;
        var restartVoiceListening = false;
        SpeechStopButton.IsEnabled = false; SpeechCancelButton.IsEnabled = true; SpeechStatusText.Text = _config.UiLanguage == "de" ? "Aufnahme wird verarbeitet · lokal mit Whisper" : "Processing recording · local Whisper";
        _voiceSpeechDetected = false; _voiceStopScheduled = false;
        if (_voiceModeEnabled)
        {
            SpeechStatusRow.Visibility = Visibility.Collapsed;
            _voiceWindow?.SetState(VoiceUiState.Thinking, "Understanding…", "Converting your speech locally.");
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
        try
        {
            var token = operation?.Token ?? CancellationToken.None;
            var wav = await _speech.StopRecordingAsync(token);
            _speechTranscribing = true;
            SpeechStatusText.Text = _config.UiLanguage == "de" ? "Sprache wird lokal erkannt …" : "Transcribing locally…";
            var transcriptionTimer = Stopwatch.StartNew();
            var text = await _speech.TranscribeAsync(wav, SpeechModelPath(), token);
            transcriptionTimer.Stop();
            Log("speech-transcription-timing", true, $"Whisper {SelectedSpeechModel}: {transcriptionTimer.Elapsed.TotalSeconds:F1}s");
            if (_voiceModeEnabled) text = StripWakePhraseFromVoicePrompt(text);
            if (_voiceModeEnabled && string.IsNullOrWhiteSpace(text))
            {
                _voiceWindow?.SetState(VoiceUiState.Listening, "Listening…", "Wake phrase detected. Say your request now.");
                restartVoiceListening = true;
                return;
            }
            PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text) ? text : PromptBox.Text.TrimEnd() + " " + text;
            PromptBox.CaretIndex = PromptBox.Text.Length; PromptBox.Focus();
            if (_voiceModeEnabled) _voiceWindow?.SetTranscript(text);
            SpeechStatusText.Text = _config.UiLanguage == "de" ? "Erkennung abgeschlossen · Text ist noch nicht gesendet" : "Transcription complete · text has not been sent"; Log("speech-transcription", true, $"{text.Length} Zeichen erkannt; Prompt nicht gesendet");
            if (_voiceModeEnabled)
            {
                SpeechStatusText.Text = _config.UiLanguage == "de"
                    ? "Sprachmodus · Anfrage wird an das gewählte Modell gesendet"
                    : "Voice mode · sending your request to the selected model";
                _voiceWindow?.SetState(VoiceUiState.Thinking, "Thinking…", "Your speech is recognized. Jarvis is asking the selected model now.");
                // Release this recording state before sending. The reply can then
                // open a fresh barge-in monitor while TTS is playing.
                _speechBusy = false;
                _speechTranscribing = false;
                _speechOperation?.Dispose();
                if (ReferenceEquals(_speechOperation, operation)) _speechOperation = null;
                _speechLastCapturePath = "";
                completion?.TrySetResult();
                handedOffToVoiceReply = true;
                await SendMessage();
            }
        }
        catch (OperationCanceledException) { SpeechStatusText.Text = _config.UiLanguage == "de" ? "Spracheingabe abgebrochen." : "Speech input cancelled."; }
        catch (Exception ex) { SpeechStatusText.Text = (_config.UiLanguage == "de" ? "Spracheingabe fehlgeschlagen: " : "Speech input failed: ") + (_config.UiLanguage == "de" ? ex.Message : "Check your microphone and selected speech model in Settings → Speech input."); SpeechDeviceStatus.Text = _config.UiLanguage == "de" ? ex.Message : "Microphone or local speech recognition failed. See the application log for details."; if (_voiceModeEnabled) _voiceWindow?.SetState(VoiceUiState.Error, "Speech recognition failed", _config.UiLanguage == "de" ? ex.Message : "Check your microphone and selected speech model in Settings → Speech input."); Log("speech-transcription", false, ex.ToString()); }
        finally
        {
            try { if (File.Exists(capturePath)) File.Delete(capturePath); } catch { }
            // SendMessage may have started a new capture for barge-in. Do not reset
            // or delete that newer capture when this older stop operation returns.
            if (ReferenceEquals(_speechOperation, operation))
            {
                _speechBusy = false; _speechTranscribing = false;
                _speechOperation?.Dispose(); _speechOperation = null; _speechLastCapturePath = "";
            }
            if (handedOffToVoiceReply || ReferenceEquals(_speechOperation, operation))
            {
                SpeechStatusRow.Visibility = Visibility.Collapsed; SpeechStopButton.Visibility = Visibility.Collapsed; SpeechCancelButton.Visibility = Visibility.Collapsed; SpeechMeter.Visibility = Visibility.Visible;
                SpeechStopButton.IsEnabled = true; SpeechCancelButton.IsEnabled = true; MicButton.IsEnabled = !_speechBusy && SpeechEnabledCheck.IsChecked == true;
                if (!_voiceModeEnabled) StatusText.Text = _config.UiLanguage == "de" ? "Bereit · lokal gespeichert" : "Ready · saved locally";
                RefreshSpeechDevices(false); completion?.TrySetResult();
            }
            if (restartVoiceListening && _voiceModeEnabled)
                _ = Dispatcher.BeginInvoke(new Action(() => Mic_Click(this, new RoutedEventArgs())), DispatcherPriority.Background);
            if (!_voiceModeEnabled && _config.WakeWordEnabled) StartWakeWordMonitoring();
        }
    }

    private string _speechLastCapturePath = "";

    private static string StripWakePhraseFromVoicePrompt(string text)
    {
        // The wake detector's short pre-roll is intentionally included so the
        // beginning of a command is not clipped. Remove only a leading invocation.
        return Regex.Replace(text.Trim(), @"^(?:(?:hey|hei|hay)\s+)?(?:jarvis|jervis|jarwis|chavez|chaves|chavis|shaves|javes)[\s,.:;!?\-–—]*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }

    private async void SpeechCancel_Click(object sender, RoutedEventArgs e)
    {
        _voiceBargeInMonitoring = false;
        _speechOperation?.Cancel(); _speech.CancelRecording();
        try { if (File.Exists(_speechLastCapturePath)) File.Delete(_speechLastCapturePath); } catch { }
        if (_speechTranscribing) { SpeechStatusText.Text = "Abbruch wird verarbeitet …"; SpeechCancelButton.IsEnabled = false; return; }
        await _speech.WaitForRecordingStopAsync();
        try { if (File.Exists(_speechLastCapturePath)) File.Delete(_speechLastCapturePath); } catch { }
        _speechBusy = false; _speechOperation?.Dispose(); _speechOperation = null; SpeechStatusRow.Visibility = Visibility.Collapsed; SpeechStopButton.Visibility = Visibility.Collapsed; SpeechCancelButton.Visibility = Visibility.Collapsed; SpeechMeter.Visibility = Visibility.Visible; MicButton.IsEnabled = SpeechEnabledCheck.IsChecked == true;
        StatusText.Text = _config.UiLanguage == "de" ? "Spracheingabe abgebrochen" : "Speech input cancelled"; _speechCompletion?.TrySetResult();
        if (!_voiceModeEnabled && _config.WakeWordEnabled) StartWakeWordMonitoring();
    }

    private void UpdateSpeechMeter(float level)
    {
        for (var i = 0; i < _speechMeters.Length; i++)
        {
            var weight = 1f - Math.Abs(i - 3.5f) / 5f;
            _speechMeters[i].Height = Math.Max(4, 4 + level * weight * 26);
        }
    }

    private void OnSpeechLevel(float level)
    {
        UpdateSpeechMeter(level);
        if (!_voiceModeEnabled || !_speechBusy || _speechTranscribing) return;
        if (_voiceBargeInMonitoring && _tts.State is LocalTtsState.Generating or LocalTtsState.Speaking)
        {
            // Conservative level gate avoids most low-level speaker bleed. A headset
            // gives the most reliable interruption behavior because no AEC is bundled.
            if (level >= 0.20f)
            {
                if (_voiceBargeInCandidateSince == DateTime.MinValue) _voiceBargeInCandidateSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - _voiceBargeInCandidateSince >= TimeSpan.FromMilliseconds(220)) BeginVoiceBargeIn();
            }
            else if (level < 0.10f) _voiceBargeInCandidateSince = DateTime.MinValue;
            return;
        }
        if (level >= 0.015f)
        {
            _voiceSpeechDetected = true;
            _voiceLastSpeechAt = DateTime.UtcNow;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose) return;
        if (_wakeWordTask is { IsCompleted: false })
        {
            e.Cancel = true;
            _wakeWordCancellation?.Cancel();
            await StopWakeWordMonitoringAsync();
            if (!_allowWindowClose) Window_Closing(sender, e);
            return;
        }
        _request?.Cancel(); _ttsDownloadOperation?.Cancel(); _tts.Stop(); StopWakeSound(); _speechDeviceTimer.Stop(); _voiceVadTimer.Stop(); _reminderTimer.Stop();
        if (_speechBusy)
        {
            e.Cancel = true;
            _speechOperation?.Cancel(); _speech.CancelRecording();
            if (!_speechTranscribing) { await _speech.WaitForRecordingStopAsync(); try { if (File.Exists(_speechLastCapturePath)) File.Delete(_speechLastCapturePath); } catch { } _speechBusy = false; _speechCompletion?.TrySetResult(); }
            if (_speechCompletion is not null) await _speechCompletion.Task;
            _speech.Dispose(); _ttsTimer.Stop(); _tts.Dispose(); _allowWindowClose = true; Close();
            return;
        }
        _speechOperation?.Cancel(); _speech.CancelRecording(); _speech.Dispose(); _ttsTimer.Stop(); _tts.Dispose();
    }
    private void SaveCurrentConfig()
    {
        var nextEndpoint = EndpointBox.Text.Trim().TrimEnd('/');
        if (!string.Equals(_config.Endpoint.TrimEnd('/'), nextEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            _http.DefaultRequestHeaders.Authorization = null;
            _config.ApiKeyProtected = "";
        }
        _config.Endpoint = nextEndpoint; var model = string.IsNullOrWhiteSpace(SelectedChatModel()) ? ModelBox.Text : SelectedChatModel();
        _config.Model = model.Trim(); ModelBox.Text = _config.Model; SetChatModel(_config.Model); _config.SaveChat = SaveChatCheck.IsChecked == true; _config.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? DefaultPrompt : SystemPromptBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            var keyBytes = Encoding.UTF8.GetBytes(ApiKeyBox.Password);
            try
            {
                _config.ApiKeyProtected = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(keyBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKeyBox.Password);
            }
            finally { CryptographicOperations.ZeroMemory(keyBytes); ApiKeyBox.Clear(); }
        }
        _config.SpeechEnabled = SpeechEnabledCheck.IsChecked == true; _config.SpeechUseWindowsDefault = SpeechUseDefaultCheck.IsChecked == true; _config.SpeechDeviceId = SpeechDeviceBox.SelectedValue?.ToString() ?? ""; _config.SpeechModel = SelectedSpeechModel;
        _config.WakeWordEnabled = WakeWordEnabledCheck.IsChecked == true; _config.WakeWordPhrase = string.IsNullOrWhiteSpace(WakeWordPhraseBox.Text) ? "Hey Jarvis" : WakeWordPhraseBox.Text.Trim(); _config.WakeSoundVolume = Math.Clamp(WakeSoundVolumeSlider.Value / 100d, 0d, 1d); _config.VoiceEndSilenceMs = (int)Math.Clamp(VoiceEndSilenceSlider.Value, 350d, 1500d);
        _config.TtsVoiceName = TtsVoiceBox.SelectedValue?.ToString() ?? _config.TtsVoiceName;
        _config.TtsPiperVoiceId = TtsPiperVoiceBox.SelectedValue?.ToString() ?? _config.TtsPiperVoiceId;
        _config.TtsVolume = Math.Clamp(TtsVolumeSlider.Value / 100d, 0d, 1d);
        _config.TtsEngine = TtsEngineBox.SelectedValue?.ToString() ?? _config.TtsEngine;
        _config.ResourceDirectory = ResourceDirectory; _tts.ResourceDirectory = ResourceDirectory; _tts.Engine = _config.TtsEngine;
        _tts.PreferredVoiceName = _config.TtsVoiceName; _tts.PiperVoiceId = _config.TtsPiperVoiceId; _tts.Volume = (float)_config.TtsVolume;
    }

    private static string ProtectLocalSecret(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string UnprotectLocalSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async void SaveMusicSettings_Click(object sender, RoutedEventArgs e)
    {
        var enteredClientId = SpotifyClientIdBox.Text.Trim();
        var clientIdChanged = !string.Equals(_config.SpotifyClientId, enteredClientId, StringComparison.Ordinal);
        _config.SpotifyClientId = enteredClientId;
        if (clientIdChanged && !string.IsNullOrWhiteSpace(_config.SpotifyRefreshTokenProtected))
        {
            _config.SpotifyRefreshTokenProtected = "";
            _config.SpotifyAccount = "";
            SpotifyConnectionStatus.Text = _config.UiLanguage == "de" ? "Die Spotify-Client-ID wurde geändert. Verbinde Spotify erneut, um diese Anwendung zu autorisieren." : "Spotify Client ID changed. Connect Spotify again to authorize this application.";
        }
        if (!string.IsNullOrWhiteSpace(AppleMusicDeveloperTokenBox.Password)) _config.AppleMusicDeveloperTokenProtected = ProtectLocalSecret(AppleMusicDeveloperTokenBox.Password.Trim());
        if (!string.IsNullOrWhiteSpace(AppleMusicUserTokenBox.Password)) _config.AppleMusicUserTokenProtected = ProtectLocalSecret(AppleMusicUserTokenBox.Password.Trim());
        _config.AppleMusicStorefront = Regex.IsMatch(AppleMusicStorefrontBox.Text.Trim(), "^[a-zA-Z]{2,3}$") ? AppleMusicStorefrontBox.Text.Trim().ToLowerInvariant() : "de";
        AppleMusicDeveloperTokenBox.Clear(); AppleMusicUserTokenBox.Clear();
        try { await PersistConfig(); SpotifyConnectionStatus.Text = SpotifyConnectionStatusText(); AppleMusicConnectionStatus.Text = AppleMusicConnectionStatusText(); SettingsStatus.Text = _config.UiLanguage == "de" ? "Musikeinstellungen gespeichert." : "Music settings saved."; }
        catch (Exception ex) { AppleMusicConnectionStatus.Text = (_config.UiLanguage == "de" ? "Musikeinstellungen konnten nicht gespeichert werden: " : "Could not save music settings: ") + ex.Message; Log("music-settings", false, ex.Message); }
    }

    private async void SpotifyConnect_Click(object sender, RoutedEventArgs e)
    {
        SpotifyClientIdBox.IsEnabled = false;
        try
        {
            var clientId = SpotifyClientIdBox.Text.Trim();
            SpotifyConnectionStatus.Text = _config.UiLanguage == "de" ? "Warte auf die Spotify-Autorisierung im Standardbrowser …" : "Waiting for Spotify authorization in your default browser…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var connected = await new SpotifyWebApi().ConnectAsync(clientId, timeout.Token);
            _config.SpotifyClientId = clientId;
            _config.SpotifyRefreshTokenProtected = ProtectLocalSecret(connected.RefreshToken); _config.SpotifyAccount = connected.Account;
            await PersistConfig(); SpotifyConnectionStatus.Text = SpotifyConnectionStatusText();
            Log("spotify-connect", true, "Spotify OAuth PKCE completed; refresh token stored using Windows DPAPI.");
        }
        catch (Exception ex) { SpotifyConnectionStatus.Text = (_config.UiLanguage == "de" ? "Spotify-Verbindung fehlgeschlagen: " : "Spotify connection failed: ") + ex.Message; Log("spotify-connect", false, ex.ToString()); }
        finally { SpotifyClientIdBox.IsEnabled = true; }
    }

    private async void SpotifyDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _config.SpotifyRefreshTokenProtected = ""; _config.SpotifyAccount = "";
        try { await PersistConfig(); SpotifyConnectionStatus.Text = SpotifyConnectionStatusText(); Log("spotify-disconnect", true, "Local Spotify refresh token removed."); }
        catch (Exception ex) { SpotifyConnectionStatus.Text = (_config.UiLanguage == "de" ? "Verbindung konnte nicht getrennt werden: " : "Could not disconnect: ") + ex.Message; }
    }
    private bool HasSpotifyConnection => !string.IsNullOrWhiteSpace(_config.SpotifyClientId) && !string.IsNullOrWhiteSpace(_config.SpotifyRefreshTokenProtected);
    private string SpotifyConnectionStatusText() => HasSpotifyConnection
        ? (_config.UiLanguage == "de" ? "Spotify verbunden · " : "Spotify connected · ") + _config.SpotifyAccount
        : string.IsNullOrWhiteSpace(_config.SpotifyClientId)
            ? (_config.UiLanguage == "de" ? "Spotify ist nicht eingerichtet. Gib die Spotify-Developer-Client-ID ein und verbinde dein Konto." : "Spotify is not configured. Enter the Spotify Developer Client ID and connect.")
            : (_config.UiLanguage == "de" ? "Spotify ist nicht verbunden. Autorisiere dieses Konto bei Spotify." : "Spotify is not connected. Authorize this account with Spotify.");
    private string AppleMusicConnectionStatusText() => string.IsNullOrWhiteSpace(_config.AppleMusicDeveloperTokenProtected)
        ? (_config.UiLanguage == "de" ? "Apple Music ist nicht eingerichtet." : "Apple Music is not configured.")
        : string.IsNullOrWhiteSpace(_config.AppleMusicUserTokenProtected)
            ? (_config.UiLanguage == "de" ? "Developer-Token gespeichert. Für persönliche Mediathek und Playlist-Änderungen wird ein Music User Token benötigt." : "Developer token saved. A Music User Token is required for personal library and playlist changes.")
            : (_config.UiLanguage == "de" ? "Apple-Music-Tokens sind sicher für dieses Windows-Konto gespeichert." : "Apple Music tokens are saved securely for this Windows account.");
    private string FriendlyEndpointError(string endpoint, Exception exception)
    {
        if (exception is InvalidOperationException) return exception.Message;
        if (IsNoModelLoaded(exception.Message)) return _config.UiLanguage == "de" ? "LM Studio meldet weiterhin, dass kein Chatmodell geladen ist. Jarvis hat versucht, das ausgewählte Modell zu laden. Lade es in LM Studio und aktualisiere danach die Modellauswahl." : "LM Studio still reports that no chat model is loaded. Jarvis tried to load the selected model. Load a model in LM Studio, then refresh the model list.";
        var socket = exception as SocketException ?? (exception as HttpRequestException)?.InnerException as SocketException;
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Authority : endpoint;
        if (socket is not null && socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
            return _config.UiLanguage == "de" ? $"Kein KI-Server unter {host} erreichbar. Starte in LM Studio den Local Server oder prüfe Basis-URL und Port. Deine Nachricht bleibt in Jarvis gespeichert; es wurde keine KI-Antwort erzeugt." : $"No AI server is reachable at {host}. Start the LM Studio local server or check the base URL and port. Your message remains saved in Jarvis; no AI response was generated.";
        if (exception is TimeoutException)
            return _config.UiLanguage == "de" ? $"LM Studio hat auf den Modellkatalog nicht rechtzeitig geantwortet ({host}). Öffne LM Studio, starte den Local Server und lade ein Chatmodell. In den Logs stehen technische Details." : $"LM Studio did not respond to the model catalog request in time ({host}). Open LM Studio, start the local server, and load a chat model. See the logs for technical details.";
        if (exception is OperationCanceledException or TaskCanceledException)
            return _config.UiLanguage == "de" ? $"Zeitüberschreitung bei {host}. Prüfe, ob der Server läuft und das Modell geladen ist." : $"Request timed out at {host}. Check that the server is running and a model is loaded.";
        return _config.UiLanguage == "de" ? $"Verbindungsfehler bei {host}: {exception.Message}\nPrüfe Server, Endpunkt und Modellname." : $"Connection error at {host}: {exception.Message}\nCheck the server, endpoint, and model name.";
    }
    private async Task PersistConfig() { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions)); }
    private Uri EndpointUri(string path) => EndpointUri(path, _config.Endpoint);
    private Uri EndpointUri(string path, string endpoint)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")) throw new InvalidOperationException(_config.UiLanguage == "de" ? "Die API-Basis-URL muss eine gültige HTTP- oder HTTPS-Adresse sein." : "The API base URL must be a valid HTTP or HTTPS address.");
        return new Uri(baseUri, path);
    }

    private async void ReminderAdd_Click(object sender, RoutedEventArgs e)
    {
        var title = ReminderTitleBox.Text.Trim();
        if (title.Length == 0 || ReminderDatePicker.SelectedDate is not DateTime date || !TimeOnly.TryParse(ReminderTimeBox.Text, out var time)) { ReminderStatus.Text = "Gib einen Titel sowie Datum und Uhrzeit im Format HH:mm ein."; return; }
        var local = date.Date.Add(time.ToTimeSpan()); var due = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        if (due <= DateTimeOffset.Now) { ReminderStatus.Text = "Der Zeitpunkt muss in der Zukunft liegen."; return; }
        _reminders.Add(new ReminderRecord(title, due, "Ausstehend", false, DateTimeOffset.Now)); ReminderTitleBox.Clear(); ReminderStatus.Text = "Erinnerung lokal gespeichert."; await PersistReminders();
    }
    private async void ReminderComplete_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderList.SelectedItem is not ReminderRecord item) return;
        var index = _reminders.IndexOf(item); _reminders[index] = item with { State = "Erledigt", Notified = true }; await PersistReminders();
    }
    private async void ReminderRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderList.SelectedItem is not ReminderRecord item) return;
        _reminders.Remove(item); await PersistReminders();
    }
    private async void ReminderTimer_Tick(object? sender, EventArgs e)
    {
        var due = _reminders.FirstOrDefault(r => r.State == "Ausstehend" && !r.Notified && r.DueAt <= DateTimeOffset.Now);
        if (due is null) return;
        var index = _reminders.IndexOf(due); _reminders[index] = due with { State = "Fällig", Notified = true }; await PersistReminders();
        MessageBox.Show(this, due.Title, "Jarvis · Erinnerung", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task PersistReminders() { try { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(RemindersPath, JsonSerializer.Serialize(_reminders, JsonOptions)); } catch (Exception ex) { Log("reminders", false, ex.Message); ReminderStatus.Text = "Erinnerungen konnten nicht gespeichert werden: " + ex.Message; } }
    private async void MemorySave_Click(object sender, RoutedEventArgs e)
    {
        var content = MemoryTextBox.Text.Trim(); if (content.Length == 0) { SettingsStatus.Text = "Eintrag darf nicht leer sein."; return; }
        var category = string.IsNullOrWhiteSpace(MemoryCategoryBox.Text) ? "Allgemein" : MemoryCategoryBox.Text.Trim();
        var record = new MemoryRecord(content, category, MemoryConfirmedCheck.IsChecked == true ? "Bestätigt" : "Vorläufig", DateTimeOffset.Now, MemoryConfirmedCheck.IsChecked == true);
        if (MemoryList.SelectedItem is MemoryRecord selected) { var index = _memories.IndexOf(selected); record = record with { CreatedAt = selected.CreatedAt }; _memories[index] = record; }
        else _memories.Insert(0, record);
        await PersistMemories(); MemoryTextBox.Clear(); RefreshMemorySummary();
    }
    private void MemoryFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MemoryList.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(MemoryList.ItemsSource); var filter = MemoryFilterBox.Text.Trim();
        view.Filter = item => item is MemoryRecord memory && (filter.Length == 0 || memory.Content.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || memory.Category.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
    }
    private void MemorySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is not MemoryRecord selected) return;
        MemoryTextBox.Text = selected.Content; MemoryCategoryBox.Text = selected.Category; MemoryConfirmedCheck.IsChecked = selected.Confirmed;
    }
    private async void MemoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MemoryList.SelectedItem is not MemoryRecord selected) return;
        if (MessageBox.Show(this, $"Diesen gespeicherten Eintrag löschen?\n\n{selected.Content}", "Memory löschen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _memories.Remove(selected); MemoryTextBox.Clear(); await PersistMemories(); RefreshMemorySummary();
    }
    private async void MemoryClear_Click(object sender, RoutedEventArgs e)
    {
        if (_memories.Count == 0) return;
        if (MessageBox.Show(this, $"Alle {_memories.Count} gespeicherten Informationen dauerhaft löschen?", "Alle Memories löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _memories.Clear(); MemoryTextBox.Clear(); await PersistMemories(); RefreshMemorySummary();
    }
    private async Task PersistMemories() { try { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(MemoryPath, JsonSerializer.Serialize(_memories, JsonOptions)); Log("memory", true, "Memory-Daten gespeichert"); } catch (Exception ex) { Log("memory", false, ex.Message); StatusText.Text = "Erinnerungen konnten nicht gespeichert werden: " + ex.Message; } }
    private void RefreshMemorySummary()
    {
        var confirmed = _memories.Where(m => m.Confirmed).ToList(); MemorySummary.Text = confirmed.Count == 0 ? "Noch keine bestätigten Informationen gespeichert." : "Das weiß Jarvis über dich:\n" + string.Join("\n", confirmed.Select(m => $"• {m.Content}"));
    }

    private static JsonElement ParseToolDefinition(string schema) { using var document = JsonDocument.Parse(schema); return document.RootElement.Clone(); }
    private static object[] ToolDefinitions() =>
    [
        // Keep the plan tool first: some OpenAI-compatible local servers/models truncate
        // advertised tool schemas, and planning must be visible before mutations begin.
        ParseToolDefinition("""{"type":"function","function":{"name":"plan_task","description":"Create the user's visible, tracked execution plan for a multi-step job. Use 1-10 ordered, concrete steps; each step should correspond to one actual tool action. Jarvis marks a step in progress before it runs and completed only after an ok:true result. On failure inspect the result, continue independent steps, and use a safe correction or alternative when justified; do not claim unfinished steps succeeded. Planning alone does not execute any action.","parameters":{"type":"object","properties":{"title":{"type":"string","maxLength":120},"steps":{"type":"array","items":{"type":"string","maxLength":300},"minItems":1,"maxItems":10}},"required":["title","steps"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"list_files","description":"List files and subfolders in an allowed directory","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"search_files","description":"Search filenames under an allowed directory","parameters":{"type":"object","properties":{"path":{"type":"string"},"pattern":{"type":"string"}},"required":["path","pattern"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"read_file","description":"Read a small text file in an allowed directory","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"create_folder","description":"Create a folder inside an allowed directory; requires user approval","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"write_file","description":"Create or update a text file inside an allowed directory; requires user approval and backs up existing files","parameters":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"open_app","description":"Open a built-in app alias, installed Windows app/game discovered from Start Menu shortcuts or App Paths, or a full EXE/LNK path. Use only for a direct user request. The app asks once for approval in the chat. No shell commands or arguments.","parameters":{"type":"object","properties":{"app_name":{"type":"string","description":"App/game name such as Fortnite or Roblox, known alias, or full EXE/LNK path"}},"required":["app_name"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"open_url","description":"Open an explicit user-requested HTTP or HTTPS web link in the Windows default browser. Jarvis asks user confirmation first.","parameters":{"type":"object","properties":{"url":{"type":"string"}},"required":["url"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"request_folder_access","description":"Ask the user for one-time in-chat permission to access an existing folder outside the current allowed directories. Call before attempting file operations there.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"open_folder","description":"Open an existing folder in File Explorer. The user confirms in the chat first.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"wait","description":"Wait asynchronously between ordered task steps. Maximum 60 seconds per call.","parameters":{"type":"object","properties":{"seconds":{"type":"integer","minimum":1,"maximum":60}},"required":["seconds"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"list_windows","description":"List visible app window titles and process names so a later close or move can target the correct window.","parameters":{"type":"object","properties":{},"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"close_window","description":"Gracefully request a matching app window to close. User confirmation is required. This does not force kill the process.","parameters":{"type":"object","properties":{"target":{"type":"string","description":"Exact process name or distinctive window-title substring"}},"required":["target"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"move_window","description":"Move a matching visible application window onto an adjacent or numbered monitor. Requires user confirmation. Direction may be left, right, primary, or monitor number 1-16.","parameters":{"type":"object","properties":{"target":{"type":"string"},"destination":{"type":"string","enum":["left","right","primary","1","2","3","4","5","6","7","8","9","10","11","12","13","14","15","16"]}},"required":["target","destination"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"maximize_window","description":"Maximize a matching visible app window using the normal Windows maximize button state. This is not exclusive full-screen mode. Requires user confirmation. Identify the window by exact title or a unique process name.","parameters":{"type":"object","properties":{"target":{"type":"string","description":"Exact visible window title or unique process name"}},"required":["target"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"spotify_control","description":"Control Spotify. Requires a connected Spotify account for play_track, like_current, and add_to_playlist. Playback start needs Spotify Premium. Volume actions change and verify only the Spotify Windows audio session. Actions: play_pause, stop, next, previous, volume_up, volume_down, get_volume, set_volume, volume_sequence, open_search, play_track, like_current, add_to_playlist.","parameters":{"type":"object","properties":{"action":{"type":"string","enum":["play_pause","stop","next","previous","seek_forward","seek_backward","volume_up","volume_down","get_volume","set_volume","volume_sequence","open_search","play_track","like_current","add_to_playlist"]},"query":{"type":"string"},"playlist":{"type":"string"},"seconds":{"type":"integer","minimum":1,"maximum":60},"percent":{"type":"integer","minimum":0,"maximum":100}},"required":["action"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"apple_music_control","description":"Search Apple Music catalog, open a found song externally, save a found song to the user's library, or add it to a named library playlist. Requires Apple Developer and Music User tokens configured in Settings > Music. Playback opened externally cannot be verified by Jarvis.","parameters":{"type":"object","properties":{"action":{"type":"string","enum":["play_track","like_track","add_to_playlist"]},"query":{"type":"string"},"playlist":{"type":"string"}},"required":["action","query"],"additionalProperties":false}}}"""),
        ParseToolDefinition("""{"type":"function","function":{"name":"google_search","description":"Search the web and return real result links/snippets, trying Google then clearly labeled Bing and DuckDuckGo fallbacks.","parameters":{"type":"object","properties":{"query":{"type":"string","maxLength":300}},"required":["query"],"additionalProperties":false}}}""")
    ];

private async Task<string> ExecuteModelToolAsync(string name, string argsJson, CancellationToken token, IReadOnlyCollection<string> allowedRoots)
{
    try
    {
        allowedRoots = _config.AllowedRoots.Concat(allowedRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        token.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(argsJson);
        string Arg(string key) => args.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : throw new InvalidDataException($"Parameter '{key}' fehlt oder ist ungültig.");
        if (name == "plan_task")
        {
            var title = Arg("title").Trim();
            if (!args.RootElement.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array || stepsElement.GetArrayLength() is < 1 or > 10)
                throw new InvalidDataException("Ein Aufgabenplan muss zwischen 1 und 10 Schritten enthalten.");
            var steps = stepsElement.EnumerateArray().Select(step => step.ValueKind == JsonValueKind.String ? step.GetString()?.Trim() ?? "" : "").ToList();
            if (title.Length == 0 || title.Length > 120 || steps.Any(step => step.Length == 0 || step.Length > 300))
                throw new InvalidDataException("Titel oder Plan-Schritt ist leer oder zu lang.");
            if (_activePlan is not null && _activePlan.Status is "In progress" or "Failed") throw new InvalidOperationException("Für diese Anfrage existiert bereits ein laufender Aufgabenplan.");
            _activePlan = new TaskExecutionPlan
            {
                Title = title,
                Status = "In progress",
                Steps = steps.Select((step, index) => new TaskExecutionPlanStep { Number = index + 1, Description = step }).ToList()
            };
            RefreshPlanMessage();
            AddTask("Plan: " + title, "In Bearbeitung", $"0/{steps.Count} Schritte abgeschlossen · Plan-ID {_activePlan.Id}");
            Log("plan_task", true, $"Plan {_activePlan.Id} erstellt: {string.Join(" → ", steps)}");
            return JsonSerializer.Serialize(new { ok = true, plan_id = _activePlan.Id, title, steps, status = "Plan wird in der Chat-Ansicht verfolgt. Führe jeden Schritt nacheinander mit den verfügbaren Werkzeugen aus." });
        }
        if (name == "open_app")
        {
            var requested = Arg("app_name").Trim();
            if (requested.Length == 0 || requested.Length > 500) throw new InvalidDataException("Ungültiger App-Name oder EXE-Pfad.");
            if (NormalizeAppName(requested) is "youtube" or "youtubemusic")
            {
                var youtube = NormalizeAppName(requested) == "youtubemusic" ? "https://music.youtube.com" : "https://www.youtube.com";
                var browserApproval = await RequestApprovalInlineAsync("YouTube im Standardbrowser öffnen?", youtube, token);
                if (!browserApproval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = browserApproval.Feedback });
                Process.Start(new ProcessStartInfo(youtube) { UseShellExecute = true });
                RecordTool(name, true, youtube); return JsonSerializer.Serialize(new { ok = true, url = youtube, opened_in_browser = true });
            }
            var game = await Task.Run(() => InstalledGameLocator.Find(requested, token), token);
            var target = game?.LaunchTarget ?? ResolveAppTarget(requested);
            var sourceDetails = game is null ? "Quelle: Windows-App-Pfade, Startmenü oder angegebene EXE/Verknüpfung." : $"Plattform: {game.Platform}\nGefundene Spiel-EXE: {game.ExecutablePath}\nStart wird über: {target}";
            var approval = await RequestApprovalInlineAsync("App öffnen?", $"Anwendung: {requested}\n{sourceDetails}\nEs werden keine beliebigen Shell-Befehle ausgeführt.", token);
            if (!approval.Approved)
            { RecordTool(name, false, $"Vom Nutzer abgelehnt: {target}; Hinweis: {approval.Feedback}"); return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback }); }
            token.ThrowIfCancellationRequested();
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch when (game is not null && !string.IsNullOrWhiteSpace(game.ExecutablePath) && !string.Equals(target, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                Process.Start(new ProcessStartInfo(game.ExecutablePath) { UseShellExecute = true });
                target = game.ExecutablePath;
            }
            RecordTool(name, true, game is null ? target : $"{game.Name} · {game.Platform} · EXE={game.ExecutablePath} · Start={target}");
            return JsonSerializer.Serialize(new { ok = true, application = requested, platform = game?.Platform, executable = game?.ExecutablePath, launch_target = target, opened = true });
        }
        if (name == "open_url")
        {
            var raw = Arg("url").Trim();
            if (raw.Length > 2048 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0)
                throw new InvalidDataException("Nur gültige HTTP- oder HTTPS-Links ohne Zugangsdaten können geöffnet werden.");
            var approval = await RequestApprovalInlineAsync("Link im Standardbrowser öffnen?", uri.AbsoluteUri, token);
            if (!approval.Approved)
            { RecordTool(name, false, $"Vom Nutzer abgelehnt: {uri.AbsoluteUri}; Hinweis: {approval.Feedback}"); return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback }); }
            token.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            RecordTool(name, true, uri.AbsoluteUri); return JsonSerializer.Serialize(new { ok = true, url = uri.AbsoluteUri, opened = true });
        }
        if (name == "request_folder_access")
        {
            var requestedPath = Arg("path").Trim();
            if (!Path.IsPathFullyQualified(requestedPath)) throw new InvalidDataException("Bitte einen vollständigen Ordnerpfad angeben.");
            var folder = Path.GetFullPath(requestedPath);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Der Ordner '{folder}' existiert nicht. Bitte prüfe den Zielpfad.");
            if (_config.AllowedRoots.Any(root => IsWithinPath(folder, root))) return JsonSerializer.Serialize(new { ok = true, path = folder, already_allowed = true });
            var approval = await RequestApprovalInlineAsync("Ordnerzugriff freigeben?", $"Jarvis möchte in diesem Ordner lesen und – nach einer zusätzlichen Bestätigung je Änderung – Dateien schreiben:\n\n{folder}\n\nDer Zugriff bleibt lokal gespeichert und kann unter Einstellungen > Datenschutz & Werkzeuge widerrufen werden.", token);
            if (!approval.Approved) { RecordTool(name, false, $"Abgelehnt: {folder}; {approval.Feedback}"); return JsonSerializer.Serialize(new { ok = false, error = "Ordnerzugriff abgelehnt.", user_guidance = approval.Feedback }); }
            if (!_config.AllowedRoots.Contains(folder, StringComparer.OrdinalIgnoreCase)) _config.AllowedRoots.Add(folder);
            await PersistConfig(); ToolRootsList.ItemsSource = null; ToolRootsList.ItemsSource = _config.AllowedRoots;
            RecordTool(name, true, $"Ordnerzugriff freigegeben: {folder}"); return JsonSerializer.Serialize(new { ok = true, path = folder, access_granted = true });
        }
        if (name == "wait")
        {
            if (!args.RootElement.TryGetProperty("seconds", out var secondsValue) || !secondsValue.TryGetInt32(out var seconds) || seconds is < 1 or > 60) throw new InvalidDataException("Wartezeit muss 1 bis 60 Sekunden betragen.");
            await Task.Delay(TimeSpan.FromSeconds(seconds), token); RecordTool(name, true, $"Asynchron {seconds} Sekunden gewartet."); return JsonSerializer.Serialize(new { ok = true, waited_seconds = seconds });
        }
        if (name == "list_windows")
        {
            var windows = EnumerateWindows().Select(w => new { w.Title, w.ProcessName, monitor = ScreenForWindow(w.Handle) }).ToArray();
            RecordTool(name, true, $"{windows.Length} sichtbare Fenster aufgelistet."); return JsonSerializer.Serialize(new { ok = true, windows });
        }
        if (name == "close_window") return await CloseWindowAsync(Arg("target"), token);
        if (name == "move_window") return await MoveWindowAsync(Arg("target"), Arg("destination"), token);
        if (name == "maximize_window") return await MaximizeWindowAsync(Arg("target"), token);
        if (name == "open_folder")
        {
            var path = Arg("path").Trim(); if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException("Der angegebene Ordner existiert nicht oder der Pfad ist ungültig.");
            var approval = await RequestApprovalInlineAsync("Ordner im Explorer öffnen?", Path.GetFullPath(path), token);
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Path.GetFullPath(path) } });
            RecordTool(name, true, Path.GetFullPath(path)); return JsonSerializer.Serialize(new { ok = true, path = Path.GetFullPath(path), opened = true });
        }
        if (name == "spotify_control") return await ExecuteSpotifyControlAsync(args.RootElement, token);
        if (name == "apple_music_control") return await ExecuteAppleMusicControlAsync(args.RootElement, token);
        if (name == "google_search")
        {
            var query = Arg("query").Trim();
            if (query.Length is 0 or > 300 || query.Any(char.IsControl)) throw new InvalidDataException("Die Google-Suchanfrage muss zwischen 1 und 300 Zeichen lang sein.");
            var result = await GoogleSearchAsync(query, token);
            using var parsedResult = JsonDocument.Parse(result);
            var provider = parsedResult.RootElement.TryGetProperty("provider", out var p) ? p.GetString() : "unbekannt";
            var count = parsedResult.RootElement.TryGetProperty("results", out var hits) && hits.ValueKind == JsonValueKind.Array ? hits.GetArrayLength() : 0;
            RecordTool(name, true, $"provider={provider}; Treffer={count}; query={Trim(query)}"); return result;
        }
        if (name == "list_files")
        {
            var path = ResolveAllowedPath(Arg("path"), true, allowedRoots);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly).Take(200).Select(p => new { name = Path.GetFileName(p), type = Directory.Exists(p) ? "directory" : "file", path = p }).ToArray();
            return JsonSerializer.Serialize(new { ok = true, entries });
        }
        if (name == "search_files")
        {
            var root = ResolveAllowedPath(Arg("path"), true, allowedRoots); var pattern = Arg("pattern");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            if (pattern.Length is 0 or > 100 || pattern.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || pattern.Contains(Path.DirectorySeparatorChar) || pattern.Contains(Path.AltDirectorySeparatorChar)) throw new InvalidDataException("Ungültiges Dateimuster.");
            var results = await Task.Run(() => SearchAllowedFiles(root, pattern, token), token);
            return JsonSerializer.Serialize(new { ok = true, results, truncated = results.Length >= 100 });
        }
        if (name == "read_file")
        {
            var path = ResolveAllowedPath(Arg("path"), true, allowedRoots); var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Datei nicht gefunden.", path);
            if (info.Length > 300_000) throw new InvalidDataException("Datei ist größer als das Leselimit von 300 KB.");
            var text = await File.ReadAllTextAsync(path, token);
            return JsonSerializer.Serialize(new { ok = true, path, content = text });
        }
        if (name == "create_folder")
        {
            var path = ResolveAllowedPath(Arg("path"), false, allowedRoots);
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Der Zielpfad existiert bereits.");
            var approval = await RequestApprovalInlineAsync("Ordner erstellen?", $"Ziel: {path}\nAuswirkung: Ein neuer Ordner wird erstellt.", token);
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(path); if (!Directory.Exists(path)) throw new IOException("Ordner wurde nicht angelegt.");
            RecordTool(name, true, path); return JsonSerializer.Serialize(new { ok = true, path, created = true });
        }
        if (name == "write_file")
        {
            var path = ResolveAllowedPath(Arg("path"), false, allowedRoots); var content = Arg("content");
            if (content.Length > 1_000_000) throw new InvalidDataException("Inhalt überschreitet das Limit von 1 MB.");
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Der übergeordnete Ordner muss bereits existieren.");
            var exists = File.Exists(path); var preview = content.Length > 700 ? content[..700] + "…" : content;
            var prompt = $"Ziel: {path}\nGröße: {Encoding.UTF8.GetByteCount(content)} Byte\n{(exists ? "Die bestehende Datei wird gesichert und ersetzt." : "Eine neue Datei wird erstellt.")}\n\nVorschau:\n{preview}";
            var approval = await RequestApprovalInlineAsync(exists ? "Dateiänderung freigeben?" : "Datei erstellen?", prompt, token);
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            token.ThrowIfCancellationRequested();
            string? backup = null;
            if (exists) { backup = path + ".jarvis-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"); File.Copy(path, backup, false); }
            var temp = Path.Combine(Path.GetDirectoryName(path)!, ".jarvis-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), token); File.Move(temp, path, exists); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            if (!File.Exists(path) || !string.Equals(await File.ReadAllTextAsync(path, token), content, StringComparison.Ordinal)) throw new IOException("Nachprüfung der gespeicherten Datei ist fehlgeschlagen.");
            RecordTool(name, true, $"{path}; Backup: {backup ?? "keiner (neue Datei)"}");
            return JsonSerializer.Serialize(new { ok = true, path, bytes = new FileInfo(path).Length, backup });
        }
        throw new InvalidDataException($"Werkzeug '{name}' ist nicht registriert.");
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { Log("tool:" + name, false, ex.Message); AddTask(name, "Fehlgeschlagen", ex.Message); return JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
}

private sealed record NativeWindow(IntPtr Handle, string Title, string ProcessName, uint ProcessId);
private static List<NativeWindow> EnumerateWindows()
{
    var found = new List<NativeWindow>();
    EnumWindows((hwnd, lParam) =>
    {
        if (!IsWindowVisible(hwnd)) return true;
        var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
        if (string.IsNullOrWhiteSpace(title.ToString())) return true;
        GetWindowThreadProcessId(hwnd, out var pid);
        string processName;
        try { using var process = Process.GetProcessById((int)pid); processName = process.ProcessName; }
        catch { return true; }
        if (!processName.Equals("Jarvis", StringComparison.OrdinalIgnoreCase)) found.Add(new NativeWindow(hwnd, title.ToString(), processName, pid));
        return true;
    }, IntPtr.Zero);
    return found;
}

private static string ScreenForWindow(IntPtr handle)
{
    var screen = System.Windows.Forms.Screen.FromHandle(handle);
    var index = Array.IndexOf(System.Windows.Forms.Screen.AllScreens, screen) + 1;
    return $"Monitor {index}{(screen.Primary ? " · primär" : "")}";
}

private async Task<string> CloseWindowAsync(string target, CancellationToken token)
{
    target = target.Trim();
    if (target.Length is < 1 or > 200) throw new InvalidDataException("Bitte einen Fenstertitel oder Prozessnamen angeben.");
    var matches = EnumerateWindows().Where(w => WindowMatchesTarget(w, target)).ToList();
    if (matches.Count == 0) throw new InvalidOperationException($"Kein sichtbares Fenster für „{target}“ gefunden.");
    if (matches.Any(w => new[] { "winlogon", "dwm", "csrss", "services", "lsass" }.Contains(w.ProcessName, StringComparer.OrdinalIgnoreCase))) throw new UnauthorizedAccessException("Windows-Systemfenster dürfen nicht geschlossen werden.");
    var approval = await RequestApprovalInlineAsync("Fenster schließen?", string.Join("\n", matches.Select(w => $"{w.Title} · {w.ProcessName}")) + "\nJarvis sendet nur die normale Schließen-Anforderung; das Programm wird nicht erzwungen beendet.", token);
    if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
    foreach (var window in matches) { token.ThrowIfCancellationRequested(); _ = SendMessage(window.Handle, WmClose, IntPtr.Zero, IntPtr.Zero); }
    var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (DateTime.UtcNow < until && matches.Any(w => IsWindow(w.Handle))) await Task.Delay(100, token);
    var remaining = matches.Where(w => IsWindow(w.Handle)).Select(w => w.Title).ToArray();
    if (remaining.Length > 0) { RecordTool("close_window", false, $"Schließen nicht bestätigt: {string.Join(", ", remaining)}"); return JsonSerializer.Serialize(new { ok = false, requested = matches.Count, closed = matches.Count - remaining.Length, remaining, error = "Mindestens ein Fenster hat die Schließen-Anforderung nicht bestätigt." }); }
    RecordTool("close_window", true, string.Join(", ", matches.Select(w => w.Title)));
    return JsonSerializer.Serialize(new { ok = true, closed = matches.Select(w => w.Title).ToArray(), method = "WM_CLOSE; graceful" });
}

private async Task<string> MoveWindowAsync(string target, string destination, CancellationToken token)
{
    target = target.Trim(); destination = destination.Trim().ToLowerInvariant();
    var windows = EnumerateWindows();
    var exactTitle = windows.FirstOrDefault(w => w.Title.Equals(target, StringComparison.OrdinalIgnoreCase));
    var titleMatch = windows.FirstOrDefault(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));
    var processMatches = windows.Where(w => WindowProcessMatches(w.ProcessName, target)).ToArray();
    var window = exactTitle ?? titleMatch ?? (processMatches.Length == 1 ? processMatches[0] : null);
    if (processMatches.Length > 1 && exactTitle is null && titleMatch is null)
        throw new InvalidOperationException($"Mehrere Fenster passen zu „{target}“. Rufe list_windows auf und verwende einen konkreten Fenstertitel: {string.Join("; ", processMatches.Select(w => w.Title))}");
    if (window is null) throw new InvalidOperationException($"Kein sichtbares Fenster für „{target}“ gefunden.");
    var screens = System.Windows.Forms.Screen.AllScreens; var current = System.Windows.Forms.Screen.FromHandle(window.Handle); System.Windows.Forms.Screen? next = null;
    if (destination == "primary") next = screens.FirstOrDefault(s => s.Primary);
    else if (int.TryParse(destination, out var monitor) && monitor >= 1 && monitor <= screens.Length) next = screens[monitor - 1];
    else if (destination is "left" or "right")
    {
        var bounds = current.Bounds;
        var candidates = screens.Where(s => s != current && Math.Max(bounds.Top, s.Bounds.Top) < Math.Min(bounds.Bottom, s.Bounds.Bottom));
        next = destination == "left"
            ? candidates.Where(s => s.Bounds.Right <= bounds.Left).OrderByDescending(s => s.Bounds.Right).FirstOrDefault()
            : candidates.Where(s => s.Bounds.Left >= bounds.Right).OrderBy(s => s.Bounds.Left).FirstOrDefault();
    }
    if (next is null) throw new InvalidOperationException($"Kein Monitor-Ziel „{destination}“ gefunden. Verfügbare Monitore: {screens.Length}.");
    var approval = await RequestApprovalInlineAsync("Fenster auf anderen Monitor verschieben?", $"{window.Title}\n{ScreenForWindow(window.Handle)} → Monitor {Array.IndexOf(screens, next) + 1}{(next.Primary ? " · primär" : "")}", token);
    if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
    token.ThrowIfCancellationRequested(); _ = ShowWindow(window.Handle, SwRestore);
    var rect = next.WorkingArea; var width = Math.Min(rect.Width, 1200); var height = Math.Min(rect.Height, 850);
    if (!SetWindowPos(window.Handle, IntPtr.Zero, rect.Left + 24, rect.Top + 24, width, height, SwpNoZOrder | SwpNoActivate)) throw new InvalidOperationException("Windows konnte das Fenster nicht verschieben.");
    var verified = string.Equals(System.Windows.Forms.Screen.FromHandle(window.Handle).DeviceName, next.DeviceName, StringComparison.OrdinalIgnoreCase);
    if (!verified) throw new InvalidOperationException("Windows hat die Fensterposition nicht auf dem Zielmonitor bestätigt.");
    RecordTool("move_window", true, $"{window.Title}: Monitor {Array.IndexOf(screens, next) + 1}");
    return JsonSerializer.Serialize(new { ok = true, title = window.Title, destination = $"Monitor {Array.IndexOf(screens, next) + 1}", verified });
}

private async Task<string> MaximizeWindowAsync(string target, CancellationToken token)
{
    target = target.Trim();
    if (target.Length is < 1 or > 200) throw new InvalidDataException("Bitte einen konkreten Fenstertitel oder Prozessnamen angeben.");
    var windows = EnumerateWindows();
    var exactTitle = windows.FirstOrDefault(w => w.Title.Equals(target, StringComparison.OrdinalIgnoreCase));
    var titleMatch = windows.FirstOrDefault(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));
    var processMatches = windows.Where(w => WindowProcessMatches(w.ProcessName, target)).ToArray();
    var window = exactTitle ?? titleMatch ?? (processMatches.Length == 1 ? processMatches[0] : null);
    if (processMatches.Length > 1 && exactTitle is null && titleMatch is null)
        throw new InvalidOperationException($"Mehrere Fenster passen zu „{target}“. Rufe list_windows auf und verwende einen konkreten Fenstertitel: {string.Join("; ", processMatches.Select(w => w.Title))}");
    if (window is null) throw new InvalidOperationException($"Kein sichtbares Fenster für „{target}“ gefunden.");
    var approval = await RequestApprovalInlineAsync("Fenster maximieren?", $"{window.Title} · {window.ProcessName}\nWindows-Maximierzustand wie über die Titelleisten-Schaltfläche; kein exklusiver Vollbildmodus.", token);
    if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
    token.ThrowIfCancellationRequested();
    _ = ShowWindow(window.Handle, SwMaximize);
    await Task.Delay(150, token);
    var verified = IsZoomed(window.Handle);
    if (!verified) throw new InvalidOperationException($"Windows hat das Maximieren von „{window.Title}“ nicht bestätigt.");
    RecordTool("maximize_window", true, $"Maximiert: {window.Title}");
    return JsonSerializer.Serialize(new { ok = true, title = window.Title, process = window.ProcessName, state = "maximized", verified = true, exclusive_fullscreen = false });
}

private static bool WindowMatchesTarget(NativeWindow window, string target) =>
    window.Title.Contains(target, StringComparison.OrdinalIgnoreCase) || WindowProcessMatches(window.ProcessName, target);

private static bool WindowProcessMatches(string processName, string target)
{
    static string Clean(string value) => Regex.Replace(value, "[^a-z0-9]", "", RegexOptions.IgnoreCase);
    var requested = Clean(Path.GetFileNameWithoutExtension(target.Trim()));
    var actual = Clean(processName);
    if (requested.Length == 0 || actual.Length == 0) return false;
    if (requested == actual || requested.StartsWith(actual, StringComparison.OrdinalIgnoreCase) || actual.StartsWith(requested, StringComparison.OrdinalIgnoreCase)) return true;
    return requested switch
    {
        "operagx" or "operabrowser" => actual is "opera" or "launcher",
        "google" or "googlebrowser" or "googlechrome" => actual is "chrome",
        "msedge" or "microsoftedge" => actual is "msedge",
        "firefoxbrowser" => actual is "firefox",
        _ => false
    };
}

private static bool IsWithinPath(string candidate, string root)
{
    var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
    var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

private async Task<string> ExecuteSpotifyControlAsync(JsonElement args, CancellationToken token)
{
    string action = args.TryGetProperty("action", out var actionValue) && actionValue.ValueKind == JsonValueKind.String ? actionValue.GetString() ?? "" : "";
    string query = args.TryGetProperty("query", out var queryValue) && queryValue.ValueKind == JsonValueKind.String ? queryValue.GetString() ?? "" : "";
    string playlist = args.TryGetProperty("playlist", out var playlistValue) && playlistValue.ValueKind == JsonValueKind.String ? playlistValue.GetString() ?? "" : "";
    int number(string name, int fallback) => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
    if (action is "play_track" or "like_current" or "add_to_playlist")
    {
        if (string.IsNullOrWhiteSpace(_config.SpotifyClientId))
            throw new InvalidOperationException("Die Spotify Developer Client ID fehlt. Trage sie unter Einstellungen > Musik ein und verbinde Spotify erneut.");
        if (string.IsNullOrWhiteSpace(_config.SpotifyRefreshTokenProtected))
            throw new InvalidOperationException("Spotify ist in diesem Jarvis-Profil nicht autorisiert. Verbinde das Konto unter Einstellungen > Musik.");
        var api = new SpotifyWebApi();
        var access = await api.RefreshAsync(_config.SpotifyClientId, UnprotectLocalSecret(_config.SpotifyRefreshTokenProtected), token);
        string trackUri; string trackName;
        if (action == "like_current")
        {
            var current = await api.ApiAsync(access, HttpMethod.Get, "me/player/currently-playing", null, token);
            using var currentJson = JsonDocument.Parse(current);
            if (!currentJson.RootElement.TryGetProperty("item", out var item) || !item.TryGetProperty("uri", out var currentUri)) throw new InvalidOperationException("Spotify meldet keinen aktuell laufenden Titel.");
            trackUri = currentUri.GetString() ?? throw new InvalidDataException("Spotify meldete keine Track-URI.");
            trackName = item.TryGetProperty("name", out var currentName) ? currentName.GetString() ?? trackUri : trackUri;
            var approval = await RequestApprovalInlineAsync("Aktuellen Spotify-Titel favorisieren?", trackName, token, "spotify-like");
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            await api.ApiAsync(access, HttpMethod.Put, "me/library?uris=" + Uri.EscapeDataString(trackUri), null, token);
            RecordTool("spotify_control", true, "Favorisiert: " + trackName);
            return JsonSerializer.Serialize(new { ok = true, action, track = trackName, uri = trackUri, verified = true });
        }
        if (string.IsNullOrWhiteSpace(query) || query.Length > 300) throw new InvalidDataException("Für Spotify fehlt ein gültiger Songtitel.");
        var search = await api.ApiAsync(access, HttpMethod.Get, "search?type=track&limit=5&q=" + Uri.EscapeDataString(query), null, token);
        using var searchJson = JsonDocument.Parse(search);
        var items = searchJson.RootElement.GetProperty("tracks").GetProperty("items");
        if (items.GetArrayLength() == 0) throw new InvalidOperationException($"Spotify hat keinen passenden Titel für „{query}“ gefunden.");
        var selected = items[0]; trackUri = selected.GetProperty("uri").GetString()!; trackName = selected.GetProperty("name").GetString() ?? query;
        var artist = string.Join(", ", selected.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        if (action == "play_track")
        {
            var approval = await RequestApprovalInlineAsync("Spotify-Titel abspielen?", $"{trackName} · {artist}", token, "spotify-play");
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            await api.ApiAsync(access, HttpMethod.Put, "me/player/play", new { uris = new[] { trackUri } }, token);
            await Task.Delay(700, token);
            var nowPlaying = await api.ApiAsync(access, HttpMethod.Get, "me/player/currently-playing", null, token);
            if (string.IsNullOrWhiteSpace(nowPlaying)) throw new InvalidOperationException("Spotify accepted the play command but reported no active playback. Check the active device and Spotify Premium.");
            using var playingJson = JsonDocument.Parse(nowPlaying);
            var verified = playingJson.RootElement.TryGetProperty("item", out var playingItem) && playingItem.TryGetProperty("uri", out var playingUri) && string.Equals(playingUri.GetString(), trackUri, StringComparison.Ordinal);
            if (!verified) throw new InvalidOperationException("Spotify hat den Startbefehl angenommen, aber den ausgewählten Titel nicht als laufend bestätigt. Prüfe ein aktives Gerät und Spotify Premium.");
            RecordTool("spotify_control", true, $"Wiedergabe verifiziert: {trackName} · {artist}");
            return JsonSerializer.Serialize(new { ok = true, action, track = trackName, artist, uri = trackUri, verified = true });
        }
        if (action == "add_to_playlist")
        {
            if (string.IsNullOrWhiteSpace(playlist) || playlist.Length > 200) throw new InvalidDataException("Der Name der Spotify-Playlist fehlt.");
            var playlistsJson = await api.ApiAsync(access, HttpMethod.Get, "me/playlists?limit=50", null, token);
            using var playlistsDoc = JsonDocument.Parse(playlistsJson);
            var playlistMatch = playlistsDoc.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault(p => p.TryGetProperty("name", out var n) && string.Equals(n.GetString()?.Trim(), playlist.Trim(), StringComparison.OrdinalIgnoreCase));
            if (playlistMatch.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Spotify-Playlist „{playlist}“ wurde unter den ersten 50 Playlists nicht gefunden.");
            var playlistId = playlistMatch.GetProperty("id").GetString()!;
            var approval = await RequestApprovalInlineAsync("Titel zur Spotify-Playlist hinzufügen?", $"{trackName} · {artist}\nPlaylist: {playlist}", token, "spotify-playlist-add");
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            await api.ApiAsync(access, HttpMethod.Post, $"playlists/{Uri.EscapeDataString(playlistId)}/items", new { uris = new[] { trackUri } }, token);
            RecordTool("spotify_control", true, $"{trackUri} zur Playlist {playlist} hinzugefügt");
            return JsonSerializer.Serialize(new { ok = true, action, track = trackName, playlist, verified = true });
        }
    }
    if (action == "open_search")
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 300) throw new InvalidDataException("Für die Spotify-Suche fehlt ein gültiger Song- oder Playlistname.");
        var approval = await RequestApprovalInlineAsync("Spotify-Suche öffnen?", query.Trim(), token);
        if (!approval.Approved) { RecordTool("spotify_control", false, $"Spotify-Suche abgelehnt: {query}"); return JsonSerializer.Serialize(new { ok = false, error = "Abgelehnt.", user_guidance = approval.Feedback }); }
        var uri = "spotify:search:" + Uri.EscapeDataString(query);
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        RecordTool("spotify_control", true, $"Spotify-Suche geöffnet: {query}");
        return JsonSerializer.Serialize(new { ok = true, action, query, opened = true, note = "Spotify-Suchergebnisse geöffnet; Song oder Playlist muss dort ausgewählt und gestartet werden." });
    }

    if (action is "get_volume" or "set_volume" or "volume_up" or "volume_down" or "volume_sequence")
    {
        var original = GetSpotifySessionVolume();
        if (action == "get_volume") return JsonSerializer.Serialize(new { ok = true, action, percent = (int)Math.Round(original * 100), scope = "Spotify-App-Audiositzung", verified = true });
        if (action == "volume_sequence")
        {
            var delaySeconds = Math.Clamp(number("seconds", 5), 1, 60);
            var approval = await RequestApprovalInlineAsync("Spotify-Lautstärke-Sequenz ausführen?", $"Spotify-App-Lautstärke: {original:P0} → 100 %; {delaySeconds} Sekunden halten; in Stufen auf 0 % und zurück auf den ursprünglichen Wert {original:P0}.", token, "spotify-volume-sequence");
            if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = approval.Feedback });
            try
            {
                SetSpotifySessionVolume(1f); await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                await RampSpotifyVolumeAsync(0f, token); await RampSpotifyVolumeAsync(original, token);
                var finalValue = GetSpotifySessionVolume();
                if (Math.Abs(finalValue - original) > .04f) throw new IOException($"Ursprüngliche Spotify-Lautstärke wurde nicht bestätigt (Ziel {original:P0}, Ist {finalValue:P0}).");
                RecordTool("spotify_control", true, $"volume_sequence verified; initial={original:P0}; final={finalValue:P0}");
                return JsonSerializer.Serialize(new { ok = true, action, initial_percent = (int)Math.Round(original * 100), final_percent = (int)Math.Round(finalValue * 100), verified = true });
            }
            catch
            {
                try { SetSpotifySessionVolume(original); } catch { }
                throw;
            }
        }
        var desired = action switch
        {
            "set_volume" => Math.Clamp(number("percent", -1), 0, 100) / 100f,
            "volume_up" => Math.Clamp(original + Math.Clamp(number("percent", 5), 1, 100) / 100f, 0f, 1f),
            _ => Math.Clamp(original - Math.Clamp(number("percent", 5), 1, 100) / 100f, 0f, 1f)
        };
        if (action == "set_volume" && number("percent", -1) is < 0 or > 100) throw new InvalidDataException("Spotify-Prozentwert muss zwischen 0 und 100 liegen.");
        var setApproval = await RequestApprovalInlineAsync("Spotify-App-Lautstärke ändern?", $"Spotify-Ausgabe wird von {original:P0} auf {desired:P0} gesetzt. Die Windows-Gesamtlautstärke bleibt unverändert.", token, "spotify-volume-set");
        if (!setApproval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Vom Nutzer abgelehnt.", user_guidance = setApproval.Feedback });
        SetSpotifySessionVolume(desired); var actual = GetSpotifySessionVolume();
        if (Math.Abs(actual - desired) > .03f) throw new IOException($"Spotify-Lautstärke konnte nicht verifiziert werden (Ziel {desired:P0}, Ist {actual:P0}).");
        RecordTool("spotify_control", true, $"{action}; target={desired:P0}; verified={actual:P0}; Spotify-App-Sitzung");
        return JsonSerializer.Serialize(new { ok = true, action, percent = (int)Math.Round(actual * 100), scope = "Spotify-App-Audiositzung", verified = true });
    }

    if (action is "seek_forward" or "seek_backward")
    { const string unavailable = "Windows stellt keine verlässliche globale Medientaste zum Vor- oder Zurückspulen bereit. Jarvis hat nichts gesendet. Das Spulen muss derzeit im Spotify-Player erfolgen."; RecordTool("spotify_control", false, unavailable); return JsonSerializer.Serialize(new { ok = false, error = unavailable }); }
    if (Process.GetProcessesByName("Spotify").Length == 0)
    { const string unavailable = "Spotify läuft gerade nicht. Starte Spotify oder bitte Jarvis, zuerst eine Spotify-Suche zu öffnen. Es wurde keine Medientaste gesendet."; RecordTool("spotify_control", false, unavailable); return JsonSerializer.Serialize(new { ok = false, error = unavailable }); }

    byte key = action switch
    {
        "play_pause" => 0xB3, // VK_MEDIA_PLAY_PAUSE
        "stop" => 0xB2, // VK_MEDIA_STOP
        "next" => 0xB0, // VK_MEDIA_NEXT_TRACK
        "previous" => 0xB1, // VK_MEDIA_PREV_TRACK
        "volume_up" => 0xAF,
        "volume_down" => 0xAE,
        _ => throw new InvalidDataException($"Spotify-Aktion '{action}' wird nicht unterstützt.")
    };
    token.ThrowIfCancellationRequested(); keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, KeyUpFlag, UIntPtr.Zero);
    RecordTool("spotify_control", true, $"Windows-Medientaste {action} gesendet");
    return JsonSerializer.Serialize(new { ok = true, action, scope = "aktive Windows-Mediensitzung", command_sent = true, verified = false, note = "Windows hat die Medientaste gesendet, aber die Spotify-Wiedergabe ließ sich nicht verifizieren. Behaupte daher keinen bestätigten Wiedergabezustand." });
}

private async Task<string> ExecuteAppleMusicControlAsync(JsonElement args, CancellationToken token)
{
    string action = args.TryGetProperty("action", out var actionValue) ? actionValue.GetString() ?? "" : "";
    string query = args.TryGetProperty("query", out var queryValue) ? queryValue.GetString()?.Trim() ?? "" : "";
    string playlist = args.TryGetProperty("playlist", out var playlistValue) ? playlistValue.GetString()?.Trim() ?? "" : "";
    if (query.Length is 0 or > 300) throw new InvalidDataException("Apple Music needs a song name.");
    var developerToken = UnprotectLocalSecret(_config.AppleMusicDeveloperTokenProtected);
    if (developerToken.Length == 0) throw new InvalidOperationException("Configure an Apple Music Developer Token in Settings > Music first.");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", developerToken);
    var storefront = Regex.IsMatch(_config.AppleMusicStorefront, "^[a-z]{2,3}$") ? _config.AppleMusicStorefront : "de";
    using var searchResponse = await client.GetAsync($"https://api.music.apple.com/v1/catalog/{storefront}/search?types=songs&limit=5&term={Uri.EscapeDataString(query)}", token);
    var searchBody = await searchResponse.Content.ReadAsStringAsync(token);
    if (!searchResponse.IsSuccessStatusCode) throw new HttpRequestException($"Apple Music catalog returned {(int)searchResponse.StatusCode}: {searchBody}");
    using var search = JsonDocument.Parse(searchBody);
    var songs = search.RootElement.GetProperty("results").GetProperty("songs").GetProperty("data");
    if (songs.GetArrayLength() == 0) throw new InvalidOperationException($"Apple Music found no song matching “{query}”.");
    var song = songs[0]; var id = song.GetProperty("id").GetString()!;
    var attributes = song.GetProperty("attributes"); var name = attributes.GetProperty("name").GetString() ?? query;
    var artist = attributes.TryGetProperty("artistName", out var artistValue) ? artistValue.GetString() ?? "" : "";
    if (action == "play_track")
    {
        var songUrl = attributes.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(songUrl) || !Uri.TryCreate(songUrl, UriKind.Absolute, out var url) || url.Host is not ("music.apple.com" or "geo.music.apple.com")) throw new InvalidDataException("Apple Music returned an unsafe or invalid song URL.");
        var approval = await RequestApprovalInlineAsync("Open Apple Music song?", $"{name} · {artist}\n{songUrl}", token, "apple-music-play");
        if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Declined.", user_guidance = approval.Feedback });
        Process.Start(new ProcessStartInfo(songUrl) { UseShellExecute = true });
        RecordTool("apple_music_control", true, $"External Apple Music URL opened: {songUrl}; playback not verifiable.");
        return JsonSerializer.Serialize(new { ok = true, action, track = name, artist, url = songUrl, opened_externally = true, playback_verified = false });
    }
    var userToken = UnprotectLocalSecret(_config.AppleMusicUserTokenProtected);
    if (userToken.Length == 0) throw new InvalidOperationException("Configure an Apple Music Music User Token in Settings > Music to modify your library or playlists.");
    client.DefaultRequestHeaders.Remove("Music-User-Token"); client.DefaultRequestHeaders.Add("Music-User-Token", userToken);
    if (action == "like_track")
    {
        var approval = await RequestApprovalInlineAsync("Add song to Apple Music library?", $"{name} · {artist}", token, "apple-music-library-add");
        if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Declined.", user_guidance = approval.Feedback });
        var response = await client.PostAsync($"https://api.music.apple.com/v1/me/library?ids={Uri.EscapeDataString(id)}", null, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Apple Music library API returned {(int)response.StatusCode}: {body}");
        RecordTool("apple_music_control", true, $"Added {id} to the Apple Music library.");
        return JsonSerializer.Serialize(new { ok = true, action, track = name, library_added = true });
    }
    if (action == "add_to_playlist")
    {
        if (playlist.Length is 0 or > 200) throw new InvalidDataException("The Apple Music playlist name is missing.");
        using var playlistResponse = await client.GetAsync("https://api.music.apple.com/v1/me/library/playlists?limit=100", token);
        var playlistBody = await playlistResponse.Content.ReadAsStringAsync(token);
        if (!playlistResponse.IsSuccessStatusCode) throw new HttpRequestException($"Apple Music playlists API returned {(int)playlistResponse.StatusCode}: {playlistBody}");
        using var playlists = JsonDocument.Parse(playlistBody);
        var match = playlists.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(p => p.TryGetProperty("attributes", out var a) && a.TryGetProperty("name", out var n) && string.Equals(n.GetString()?.Trim(), playlist, StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Apple Music playlist “{playlist}” was not found among the first 100 library playlists.");
        var playlistId = match.GetProperty("id").GetString()!;
        var approval = await RequestApprovalInlineAsync("Add song to Apple Music playlist?", $"{name} · {artist}\nPlaylist: {playlist}", token, "apple-music-playlist-add");
        if (!approval.Approved) return JsonSerializer.Serialize(new { ok = false, error = "Declined.", user_guidance = approval.Feedback });
        using var addResponse = await client.PostAsync($"https://api.music.apple.com/v1/me/library/playlists/{Uri.EscapeDataString(playlistId)}/tracks", new StringContent(JsonSerializer.Serialize(new { data = new[] { new { id, type = "songs" } } }), Encoding.UTF8, "application/json"), token);
        var addBody = await addResponse.Content.ReadAsStringAsync(token);
        if (!addResponse.IsSuccessStatusCode) throw new HttpRequestException($"Apple Music playlist API returned {(int)addResponse.StatusCode}: {addBody}");
        RecordTool("apple_music_control", true, $"Added song {id} to playlist {playlist}.");
        return JsonSerializer.Serialize(new { ok = true, action, track = name, playlist, added = true });
    }
    throw new InvalidDataException($"Unsupported Apple Music action: {action}");
}

private static float GetSpotifySessionVolume() => FindSpotifySessionVolume(out var value) ? value : throw new InvalidOperationException("Keine aktive Spotify-Audiositzung gefunden. Starte die Wiedergabe in Spotify und versuche es erneut.");
private static void SetSpotifySessionVolume(float value)
{
    using var enumerator = new MMDeviceEnumerator();
    var matched = 0;
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        using (device)
        {
            var manager = device.AudioSessionManager;
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (!IsSpotifySession(session)) continue;
                using var volume = session.SimpleAudioVolume; volume.Volume = Math.Clamp(value, 0f, 1f); matched++;
            }
        }
    }
    if (matched == 0) throw new InvalidOperationException("Keine Spotify-Audiositzung gefunden. Starte Spotify und spiele einen Titel ab; Windows-Systemlautstärke wurde nicht verändert.");
}
private static bool FindSpotifySessionVolume(out float value)
{
    using var enumerator = new MMDeviceEnumerator();
    float? inactiveFallback = null;
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        using (device)
        {
            var manager = device.AudioSessionManager;
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (!IsSpotifySession(session)) continue;
                using var volume = session.SimpleAudioVolume;
                if (string.Equals(session.State.ToString(), "Active", StringComparison.OrdinalIgnoreCase)) { value = volume.Volume; return true; }
                inactiveFallback ??= volume.Volume;
            }
        }
    }
    value = inactiveFallback ?? 0; return inactiveFallback.HasValue;
}
private static bool IsSpotifySession(AudioSessionControl session)
{
    try { var pid = checked((int)session.GetProcessID); using var process = Process.GetProcessById(pid); return process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase); }
    catch { return false; }
}
private static async Task RampSpotifyVolumeAsync(float target, CancellationToken token)
{
    var start = GetSpotifySessionVolume(); const int steps = 12;
    for (var i = 1; i <= steps; i++) { token.ThrowIfCancellationRequested(); SetSpotifySessionVolume(start + (target - start) * i / steps); await Task.Delay(90, token); }
    SetSpotifySessionVolume(target);
}

private static string ResolveAppTarget(string requested)
{
    var normalized = NormalizeAppName(requested);
    var allowUninstaller = normalized.Contains("uninstall", StringComparison.Ordinal) || normalized.Contains("deinstall", StringComparison.Ordinal);
    var knownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["rechner"] = "calc.exe", ["taschenrechner"] = "calc.exe", ["calculator"] = "calc.exe", ["calc"] = "calc.exe",
        ["editor"] = "notepad.exe", ["notepad"] = "notepad.exe",
        ["paint"] = "mspaint.exe", ["malen"] = "mspaint.exe", ["mspaint"] = "mspaint.exe",
        ["explorer"] = "explorer.exe", ["dateiexplorer"] = "explorer.exe"
    };
    if (knownApps.TryGetValue(normalized, out var alias)) return alias;
    if (normalized is "einstellungen" or "settings") return "ms-settings:";
    var path = requested.Trim().Trim('"');
    if (Path.IsPathFullyQualified(path) && File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && (allowUninstaller || !IsUninstallerExecutable(path))) return Path.GetFullPath(path);
    if (Path.IsPathFullyQualified(path) && File.Exists(path) && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
    {
        var shortcutTarget = ResolveShortcutTarget(path);
        if (shortcutTarget is null) throw new InvalidDataException("Das Verknüpfungsziel konnte nicht geprüft werden. Jarvis startet aus Sicherheitsgründen keine unbekannte Verknüpfung.");
        if (!allowUninstaller && IsUninstallerExecutable(shortcutTarget)) throw new UnauthorizedAccessException("Diese Verknüpfung öffnet einen Uninstaller. Jarvis startet sie nicht.");
        return Path.GetFullPath(path);
    }
    if (Path.IsPathFullyQualified(path) && File.Exists(path) && IsUninstallerExecutable(path) && !allowUninstaller) throw new UnauthorizedAccessException("Uninstaller werden aus Sicherheitsgründen nicht gestartet. Bitte nenne ausdrücklich den Uninstaller, wenn du ihn wirklich ausführen möchtest.");
    var installedShortcut = FindInstalledAppShortcut(requested, allowUninstaller);
    if (installedShortcut is not null) return installedShortcut;
    var registryApp = FindRegisteredAppPath(requested, allowUninstaller);
    if (registryApp is not null) return registryApp;
    var installedProgram = FindInstalledProgram(requested, allowUninstaller);
    if (installedProgram is not null) return installedProgram;
    var knownPath = FindKnownInstallPath(normalized);
    if (knownPath is not null) return knownPath;
    throw new FileNotFoundException($"Jarvis hat '{requested}' nicht als installierte App gefunden. Geprüft wurden Epic-Spielmanifeste, Steam-Bibliotheken, bekannte Riot-Installationen, Startmenü, Desktop, Windows-App-Pfade und registrierte Installationen. Installiere die App oder gib ihren EXE-Pfad an.");
}

private static string NormalizeAppName(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

private static string? FindInstalledAppShortcut(string requested, bool allowUninstaller = false)
{
    var roots = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    var name = NormalizeAppName(requested);
    if (name.Length < 2) return null;
    var files = new List<string>();
    foreach (var root in roots)
    {
        try { files.AddRange(Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(Math.Max(0, 15000 - files.Count))); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        if (files.Count >= 15000) break;
    }
    var exact = files.FirstOrDefault(file => string.Equals(NormalizeAppName(Path.GetFileNameWithoutExtension(file)), name, StringComparison.Ordinal) && IsSafeShortcut(file, allowUninstaller));
    if (exact is not null) return exact;
    var close = files.FirstOrDefault(file =>
    {
        var shortcutName = NormalizeAppName(Path.GetFileNameWithoutExtension(file));
        return shortcutName.Length >= 3 && IsSafeShortcut(file, allowUninstaller) && (shortcutName.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(shortcutName, StringComparison.Ordinal));
    });
    return close;
}

private static bool IsSafeShortcut(string path, bool allowUninstaller)
{
    if (allowUninstaller) return true;
    var target = ResolveShortcutTarget(path);
    return !string.IsNullOrWhiteSpace(target) && !IsUninstallerExecutable(target);
}

private static string? ResolveShortcutTarget(string path)
{
    object? shell = null; object? shortcut = null;
    try
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
        if (shellType is null) return null;
        shell = Activator.CreateInstance(shellType);
        if (shell is null) return null;
        shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
        return shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
    }
    catch { return null; }
    finally
    {
        if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
        if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }
}

private static string? FindRegisteredAppPath(string requested, bool allowUninstaller = false)
{
    var name = NormalizeAppName(Path.GetFileNameWithoutExtension(requested));
    if (name.Length < 3) return null;
    foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null) continue;
                foreach (var appKeyName in appPaths.GetSubKeyNames())
                {
                    var appName = NormalizeAppName(Path.GetFileNameWithoutExtension(appKeyName));
                    if (!string.Equals(appName, name, StringComparison.Ordinal)) continue;
                    using var appKey = appPaths.OpenSubKey(appKeyName);
                    if (appKey?.GetValue(null) is string executable && Path.IsPathFullyQualified(executable) && File.Exists(executable) && (allowUninstaller || !IsUninstallerExecutable(executable))) return executable;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
    }
    return null;
}

private static string? FindInstalledProgram(string requested, bool allowUninstaller = false)
{
    var wanted = NormalizeAppName(requested);
    if (wanted.Length < 3) return null;
    foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var subName in uninstall.GetSubKeyNames())
            {
                using var app = uninstall.OpenSubKey(subName);
                var displayName = app?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                if (!allowUninstaller && (displayName.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || displayName.Contains("deinstall", StringComparison.OrdinalIgnoreCase))) continue;
                var candidate = NormalizeAppName(displayName);
                if (candidate != wanted && !candidate.StartsWith(wanted, StringComparison.Ordinal) && !wanted.StartsWith(candidate, StringComparison.Ordinal)) continue;
                var installLocation = app?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                {
                    var matchingExe = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(x => allowUninstaller || !IsUninstallerExecutable(x))
                        .OrderByDescending(x => NormalizeAppName(Path.GetFileNameWithoutExtension(x)) == wanted)
                        .ThenByDescending(x => NormalizeAppName(Path.GetFileNameWithoutExtension(x)).StartsWith(wanted, StringComparison.Ordinal))
                        .FirstOrDefault(x =>
                        {
                            var exeName = NormalizeAppName(Path.GetFileNameWithoutExtension(x));
                            return exeName == wanted || exeName.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(exeName, StringComparison.Ordinal);
                        });
                    if (matchingExe is not null) return matchingExe;
                }
                var icon = app?.GetValue("DisplayIcon") as string;
                if (!string.IsNullOrWhiteSpace(icon))
                {
                    var candidatePath = icon.Trim().Trim('"').Split(',')[0];
                    if (Path.IsPathFullyQualified(candidatePath) && File.Exists(candidatePath) && Path.GetExtension(candidatePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) && (allowUninstaller || !IsUninstallerExecutable(candidatePath))) return candidatePath;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }
    return null;
}

private static bool IsUninstallerExecutable(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    return name.Contains("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("deinstall", StringComparison.OrdinalIgnoreCase);
}

private static string? FindKnownInstallPath(string normalizedName)
{
    if (normalizedName is not ("epicgames" or "epicgameslauncher" or "steam")) return null;
    var exeName = normalizedName == "steam" ? "steam.exe" : "EpicGamesLauncher.exe";
    var roots = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    }.Where(x => !string.IsNullOrWhiteSpace(x));
    foreach (var root in roots)
    {
        var candidate = normalizedName == "steam"
            ? Path.Combine(root, "Steam", exeName)
            : Path.Combine(root, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", exeName);
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

private static string ResolveAllowedPath(string rawPath, bool mustExist, IReadOnlyCollection<string> allowedRoots)
{
    if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath)) throw new InvalidDataException("Es ist ein absoluter Pfad erforderlich.");
    var path = Path.GetFullPath(rawPath);
    var root = allowedRoots.Select(Path.GetFullPath).FirstOrDefault(r => path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    if (root is null) throw new UnauthorizedAccessException("Der Pfad liegt außerhalb der freigegebenen Ordner.");
    var relative = Path.GetRelativePath(root, path); var current = root;
    if (File.Exists(current) || Directory.Exists(current)) RejectReparsePoint(current);
    if (relative != ".") foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    {
        if (segment is ".." or ".") throw new UnauthorizedAccessException("Ungültiger relativer Pfad.");
        current = Path.Combine(current, segment);
        if (File.Exists(current) || Directory.Exists(current)) RejectReparsePoint(current);
    }
    if (mustExist && !(File.Exists(path) || Directory.Exists(path))) throw new FileNotFoundException("Ziel wurde nicht gefunden.", path);
    return path;
}
private static void RejectReparsePoint(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Symbolische Links und Reparse Points sind für Agentenwerkzeuge gesperrt."); }
private static string[] SearchAllowedFiles(string root, string pattern, CancellationToken token)
{
    var pending = new Stack<string>(); pending.Push(root); var results = new List<string>(); var visited = 0;
    while (pending.Count > 0 && results.Count < 100 && visited++ < 1000)
    {
        token.ThrowIfCancellationRequested(); var folder = pending.Pop();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Take(100 - results.Count)) results.Add(file);
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly)) { try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) pending.Push(dir); } catch { } }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }
    return results.ToArray();
}
private async void AddRoot_Click(object sender, RoutedEventArgs e)
{
    var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Ordner für Jarvis-Dateiwerkzeuge freigeben" };
    if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
    var root = Path.GetFullPath(picker.SelectedPath);
    if (!_config.AllowedRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) _config.AllowedRoots.Add(root);
    ToolRootsList.ItemsSource = null; ToolRootsList.ItemsSource = _config.AllowedRoots;
    try { await PersistConfig(); } catch (Exception ex) { ToolError("Freigabe speichern", ex); }
}
private async void RemoveRoot_Click(object sender, RoutedEventArgs e)
{
    if (ToolRootsList.SelectedItem is not string root) return;
    _config.AllowedRoots.Remove(root); ToolRootsList.ItemsSource = null; ToolRootsList.ItemsSource = _config.AllowedRoots;
    try { await PersistConfig(); } catch (Exception ex) { ToolError("Freigabe speichern", ex); }
}

private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try { var path = ProgramPath.Text.Trim().Trim('"'); if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Bitte den vollständigen Pfad zu einer vorhandenen EXE-Datei angeben."); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); RecordTool("Programm starten", true, path); }
        catch (Exception ex) { ToolError("Programm starten", ex); }
    }

    private static string ApprovalKey(string title, string details) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title.Trim() + "\0" + details.Trim())));
    private void RefreshTrustedApprovals()
    {
        if (TrustedApprovalsList is null || _config.TrustedApprovals is null) return;
        var query = TrustSearchBox?.Text.Trim() ?? "";
        TrustedApprovalsList.ItemsSource = _config.TrustedApprovals
            .Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Details.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    private void TrustSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshTrustedApprovals();
    private async void TrustRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (TrustedApprovalsList?.SelectedItem is not TrustedApprovalRecord selected) return;
        _config.TrustedApprovals.RemoveAll(item => string.Equals(item.Key, selected.Key, StringComparison.Ordinal));
        try { await PersistConfig(); RefreshTrustedApprovals(); SettingsStatus.Text = _config.UiLanguage == "de" ? "Dauerhafte Freigabe widerrufen." : "Persistent approval revoked."; }
        catch (Exception ex) { Log("approval-trust-revoke", false, ex.ToString()); SettingsStatus.Text = "Freigabe konnte nicht widerrufen werden: " + ex.Message; }
    }
    private void PickFile_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = false, Title = "Datei zum Öffnen auswählen" }; if (dialog.ShowDialog(this) == true) OpenPath.Text = dialog.FileName; }
    private void OpenPath_Click(object sender, RoutedEventArgs e)
    {
        try { var path = OpenPath.Text.Trim().Trim('"'); if (!Path.IsPathFullyQualified(path) || !(File.Exists(path) || Directory.Exists(path))) throw new FileNotFoundException("Datei oder Ordner wurde nicht gefunden."); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); RecordTool("Datei/Ordner öffnen", true, path); }
        catch (Exception ex) { ToolError("Datei/Ordner öffnen", ex); }
    }
    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        try { var raw = UrlBox.Text.Trim(); if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidOperationException("Nur gültige HTTP- oder HTTPS-Links sind erlaubt."); Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); RecordTool("Webseite öffnen", true, uri.AbsoluteUri); }
        catch (Exception ex) { ToolError("Webseite öffnen", ex); }
    }
    private async void SearchFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Ordner auswählen, der durchsucht werden darf" }; if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try { var pattern = SearchBox.Text.Trim(); if (string.IsNullOrWhiteSpace(pattern) || pattern.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Ungültiges Suchmuster."); var files = await Task.Run(() => Directory.EnumerateFiles(picker.SelectedPath, pattern, SearchOption.TopDirectoryOnly).Take(100).ToArray()); SearchResults.Text = files.Length == 0 ? "Keine Treffer im ausgewählten Ordner." : string.Join("\n", files); RecordTool("Dateien suchen", true, $"{files.Length} Treffer in {picker.SelectedPath}"); }
        catch (Exception ex) { SearchResults.Text = "Suche fehlgeschlagen: " + ex.Message; ToolError("Dateien suchen", ex); }
    }
    private void RecordTool(string name, bool success, string detail) { Log(name, success, detail); AddTask(name, success ? "Abgeschlossen" : "Fehlgeschlagen", detail); }
    private void ToolError(string name, Exception ex) { StatusText.Text = name + " fehlgeschlagen"; RecordTool(name, false, ex.Message); MessageBox.Show(this, ex.Message, "Jarvis · " + name, MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void AddTask(string name, string state, string result) { _tasks.Insert(0, new TaskRecord(name, state, DateTimeOffset.Now, result)); while (_tasks.Count > 250) _tasks.RemoveAt(_tasks.Count - 1); _ = PersistTasks(); }
    private async Task PersistTasks() { try { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(TasksPath, JsonSerializer.Serialize(_tasks, JsonOptions)); } catch (Exception ex) { Log("task-storage", false, ex.Message); } }
    private void Log(string action, bool success, string detail) { try { Directory.CreateDirectory(_root); File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}\t{action}\t{(success ? "OK" : "ERROR")}\t{detail.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}"); } catch { } }
    private static string Trim(string text) => text.Length > 500 ? text[..500] + "…" : text;
}

public sealed class AppConfig { public string Endpoint { get; set; } = "http://localhost:1234/v1"; public string Model { get; set; } = ""; public string ApiKeyProtected { get; set; } = ""; public bool SaveChat { get; set; } = true; public string SystemPrompt { get; set; } = ""; public ObservableCollection<string> AllowedRoots { get; set; } = []; public List<TrustedApprovalRecord> TrustedApprovals { get; set; } = []; public string DisplayName { get; set; } = "Felix FGF"; public string ProfileImagePath { get; set; } = ""; public List<string> OpenTabIds { get; set; } = []; public string ActiveChatId { get; set; } = ""; public List<string> RecentWorkspaces { get; set; } = []; public bool SpeechEnabled { get; set; } = true; public bool SpeechUseWindowsDefault { get; set; } = true; public string SpeechDeviceId { get; set; } = ""; public string SpeechModel { get; set; } = "base"; public bool WakeWordEnabled { get; set; } public string WakeWordPhrase { get; set; } = "Hey Jarvis"; public double WakeSoundVolume { get; set; } = 0.8d; public int VoiceEndSilenceMs { get; set; } = 600; public string TtsVoiceName { get; set; } = ""; public string TtsEngine { get; set; } = "windows"; public string TtsPiperVoiceId { get; set; } = "de_DE-thorsten-high"; public double TtsVolume { get; set; } = 1d; public string ResourceDirectory { get; set; } = ""; public string UiLanguage { get; set; } = ""; public string UpdateChannel { get; set; } = ""; public string SpotifyClientId { get; set; } = ""; public string SpotifyRefreshTokenProtected { get; set; } = ""; public string SpotifyAccount { get; set; } = ""; public string AppleMusicDeveloperTokenProtected { get; set; } = ""; public string AppleMusicUserTokenProtected { get; set; } = ""; public string AppleMusicStorefront { get; set; } = "de"; }
public sealed record TrustedApprovalRecord(string Key, string Title, string Details) { public string Label => $"{Title} · {Details}"; }
public sealed record ChatModelOption(string Id, string DisplayName, bool IsLoaded, bool CanLoad, string? LoadedInstanceId = null);
public sealed record ChatExchange(HttpResponseMessage Response, string Body, string ModelId) : IDisposable { public void Dispose() => Response.Dispose(); }
public sealed record TaskRecord(string Name, string State, DateTimeOffset CreatedAt, string Result);
public sealed record MemoryRecord(string Content, string Category, string Confidence, DateTimeOffset CreatedAt, bool Confirmed);
public sealed record ReminderRecord(string Title, DateTimeOffset DueAt, string State, bool Notified, DateTimeOffset CreatedAt);





















