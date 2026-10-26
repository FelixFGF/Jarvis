using System.Windows;
using Jarvis.Services;

namespace Jarvis;
public partial class MainWindow
{
    private CancellationTokenSource? _cloudVoicesRequest;
    private bool _cloudVoicePreviewRunning;
    private void ApplyCloudVoiceSettings()
    {
        _tts.CloudApiKey = () => string.IsNullOrEmpty(_config.ElevenLabsApiKeyProtected) ? "" : UnprotectLocalSecret(_config.ElevenLabsApiKeyProtected);
        _tts.CloudVoiceId = _config.ElevenLabsVoiceId;
        _tts.Speed = _config.TtsSpeed;
    }
    private void SaveCloudVoiceSettings()
    {
        if (!string.IsNullOrWhiteSpace(ElevenLabsKeyBox.Password))
        {
            _config.ElevenLabsApiKeyProtected = ProtectLocalSecret(ElevenLabsKeyBox.Password.Trim());
            ElevenLabsKeyBox.Clear();
        }
        _config.ElevenLabsVoiceId = ElevenLabsVoiceIdBox.Text.Trim();
        _config.TtsSpeed = TtsSpeedSlider.Value;
        _config.TtsAutoPlay = TtsAutoPlayCheck.IsChecked == true;
        ApplyCloudVoiceSettings();
    }
    private void ApplyCloudVoicePreview()
    {
        _tts.Engine = TtsEngineBox.SelectedValue?.ToString() ?? "windows";
        _tts.CloudVoiceId = ElevenLabsVoiceIdBox.Text.Trim();
        _tts.Speed = TtsSpeedSlider.Value;
        var previewKey = !string.IsNullOrWhiteSpace(ElevenLabsKeyBox.Password) ? ElevenLabsKeyBox.Password.Trim()
            : string.IsNullOrEmpty(_config.ElevenLabsApiKeyProtected) ? "" : UnprotectLocalSecret(_config.ElevenLabsApiKeyProtected);
        _tts.CloudApiKey = () => previewKey;
    }
    private async void LoadCloudVoices_Click(object sender, RoutedEventArgs e)
        => await LoadCloudVoicesAsync(true);

    private async void TestCloudVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudVoicePreviewRunning) return;
        _cloudVoicePreviewRunning = true;
        TestCloudVoiceButton.IsEnabled = false;
        ElevenLabsVoicesProgress.Visibility = Visibility.Visible;
        try
        {
            ApplyCloudVoicePreview();
            _tts.Engine = "elevenlabs";
            var selected = ElevenLabsVoiceBox.SelectedValue?.ToString() ?? ElevenLabsVoiceIdBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(selected)) throw new ElevenLabsException(ElevenLabsErrorKind.InvalidVoice, "No ElevenLabs voice is selected.");
            _tts.CloudVoiceId = selected;
            TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Stimmprobe wird erzeugt und abgespielt …" : "Generating and playing voice preview…";
            await _tts.SpeakAsync(_config.UiLanguage == "de"
                ? "Hallo, ich bin Jarvis. Das ist eine kurze Probe der ausgewählten Stimme."
                : "Hello, I am Jarvis. This is a short preview of the selected voice.", _config.UiLanguage);
            TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Stimmprobe abgeschlossen." : "Voice preview finished.";
        }
        catch (Exception ex)
        {
            var cloudError = FindElevenLabsError(ex);
            TtsVoiceStatus.Text = cloudError is null
                ? (_config.UiLanguage == "de" ? "Die Stimmprobe konnte nicht abgespielt werden." : "The voice preview could not be played.")
                : LocalizeElevenLabsError(cloudError.Kind);
            Log("cloud-voice-preview", false, cloudError?.Kind.ToString() ?? ex.GetType().Name);
        }
        finally
        {
            _cloudVoicePreviewRunning = false;
            TestCloudVoiceButton.IsEnabled = true;
            ElevenLabsVoicesProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static ElevenLabsException? FindElevenLabsError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is ElevenLabsException cloud) return cloud;
        return null;
    }

    private async Task LoadCloudVoicesAsync(bool persistSelection)
    {
        if (_cloudVoicesRequest is not null) return;
        _cloudVoicesRequest = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        LoadCloudVoicesButton.IsEnabled = false;
        ElevenLabsVoicesProgress.Visibility = Visibility.Visible;
        TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "ElevenLabs-Stimmen werden geladen …" : "Loading ElevenLabs voices …";
        try
        {
            var key = string.IsNullOrWhiteSpace(ElevenLabsKeyBox.Password)
                ? string.IsNullOrEmpty(_config.ElevenLabsApiKeyProtected) ? "" : UnprotectLocalSecret(_config.ElevenLabsApiKeyProtected)
                : ElevenLabsKeyBox.Password.Trim();
            using var cloud = new ElevenLabsTtsService();
            var preferredId = new[] { ElevenLabsVoiceBox.SelectedValue?.ToString(), ElevenLabsVoiceIdBox.Text, _config.ElevenLabsVoiceId }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
            var voices = await cloud.GetVoicesAsync(key, _cloudVoicesRequest.Token);
            var selectedId = ElevenLabsTtsService.ResolvePreferredVoiceId(voices, preferredId) ?? voices[0].Id;
            await Dispatcher.InvokeAsync(() =>
            {
                ElevenLabsVoiceBox.ItemsSource = voices;
                ElevenLabsVoiceBox.SelectedValue = selectedId;
                ElevenLabsVoiceIdBox.Text = selectedId;
            });
            var changed = !_config.ElevenLabsVoiceId.Equals(selectedId, StringComparison.Ordinal);
            _config.ElevenLabsVoiceId = selectedId; _tts.CloudVoiceId = selectedId;
            if (changed && persistSelection) await PersistConfig();
            TtsVoiceStatus.Text = _config.UiLanguage == "de" ? $"ElevenLabs-Verbindung geprüft · {voices.Count} Stimmen geladen." : $"ElevenLabs connection verified · {voices.Count} voices loaded.";
        }
        catch (ElevenLabsException ex)
        {
            TtsVoiceStatus.Text = LocalizeElevenLabsError(ex.Kind);
            Log("cloud-voices", false, ex.Kind.ToString());
        }
        catch (OperationCanceledException) { TtsVoiceStatus.Text = LocalizeElevenLabsError(ElevenLabsErrorKind.Timeout); Log("cloud-voices", false, "Timeout"); }
        catch (Exception ex) { TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "ElevenLabs ist derzeit nicht erreichbar. Du kannst den Abruf erneut versuchen." : "ElevenLabs is currently unavailable. You can retry loading voices."; Log("cloud-voices", false, ex.GetType().Name); }
        finally
        {
            _cloudVoicesRequest.Dispose(); _cloudVoicesRequest = null;
            LoadCloudVoicesButton.IsEnabled = true;
            ElevenLabsVoicesProgress.Visibility = Visibility.Collapsed;
        }
    }
    private void OpenElevenLabs_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ElevenLabsTtsService.OfficialApiKeysUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { TtsVoiceStatus.Text = _config.UiLanguage == "de" ? "Die ElevenLabs-Webseite konnte nicht geöffnet werden." : "The ElevenLabs website could not be opened."; Log("open-elevenlabs", false, ex.GetType().Name); }
    }
    private string LocalizeElevenLabsError(ElevenLabsErrorKind kind) => (_config.UiLanguage == "de", kind) switch
    {
        (true, ElevenLabsErrorKind.MissingKey) => "ElevenLabs: API-Schlüssel fehlt.",
        (true, ElevenLabsErrorKind.InvalidVoice) => "ElevenLabs: Ungültige Stimmen-ID.",
        (true, ElevenLabsErrorKind.Authentication) => "ElevenLabs: Schlüssel oder Berechtigung ungültig.",
        (true, ElevenLabsErrorKind.InsufficientAccess) => "ElevenLabs: Dem API-Schlüssel fehlt die Berechtigung zum Lesen der Stimmen.",
        (true, ElevenLabsErrorKind.Quota) => "ElevenLabs: Raten- oder Nutzungslimit erreicht.",
        (true, ElevenLabsErrorKind.Connectivity) => "ElevenLabs: Keine Verbindung. Prüfe Netzwerk und Firewall und versuche es erneut.",
        (true, ElevenLabsErrorKind.Timeout) => "ElevenLabs: Zeitüberschreitung. Versuche das Laden erneut.",
        (true, ElevenLabsErrorKind.Server) => "ElevenLabs: Der Dienst meldet derzeit einen Serverfehler.",
        (true, ElevenLabsErrorKind.Parsing) => "ElevenLabs: Die Stimmenantwort konnte nicht gelesen werden.",
        (true, ElevenLabsErrorKind.NoVoices) => "ElevenLabs: Für dieses Konto wurden keine verfügbaren Stimmen geliefert.",
        (true, _) => "ElevenLabs: Dienst derzeit nicht verfügbar.",
        (false, ElevenLabsErrorKind.MissingKey) => "ElevenLabs: API key is missing.",
        (false, ElevenLabsErrorKind.InvalidVoice) => "ElevenLabs: Invalid voice ID.",
        (false, ElevenLabsErrorKind.Authentication) => "ElevenLabs: Invalid key or account permission.",
        (false, ElevenLabsErrorKind.InsufficientAccess) => "ElevenLabs: The API key does not have permission to read voices.",
        (false, ElevenLabsErrorKind.Quota) => "ElevenLabs: Rate or usage limit reached.",
        (false, ElevenLabsErrorKind.Connectivity) => "ElevenLabs: Could not connect. Check the network or firewall and retry.",
        (false, ElevenLabsErrorKind.Timeout) => "ElevenLabs: The request timed out. Retry loading the voices.",
        (false, ElevenLabsErrorKind.Server) => "ElevenLabs: The service is currently reporting a server error.",
        (false, ElevenLabsErrorKind.Parsing) => "ElevenLabs: The voice response could not be read.",
        (false, ElevenLabsErrorKind.NoVoices) => "ElevenLabs: No available voices were returned for this account.",
        _ => "ElevenLabs: Service currently unavailable."
    };
    internal async Task EnsureCloudVoicesLoadedAsync()
    {
        if (TtsEngineBox?.SelectedValue?.ToString() != "elevenlabs" || string.IsNullOrWhiteSpace(_config.ElevenLabsApiKeyProtected) || ElevenLabsVoiceBox.Items.Count > 0) return;
        await LoadCloudVoicesAsync(true);
    }
    private async void ClearCloudKey_Click(object sender, RoutedEventArgs e)
    {
        _tts.Stop();
        _config.ElevenLabsApiKeyProtected = ""; ElevenLabsKeyBox.Clear();
        ApplyCloudVoiceSettings();
        try { await PersistConfig(); TtsVoiceStatus.Text = UiLocalization.Translate("Cloud-Schlüssel entfernt.", _config.UiLanguage); }
        catch (Exception) { TtsVoiceStatus.Text = UiLocalization.Translate("Speichern fehlgeschlagen.", _config.UiLanguage); }
    }
    private void StopVoice_Click(object sender, RoutedEventArgs e) => _tts.Stop();
    private void ClearPersonalInstructions_Click(object sender, RoutedEventArgs e) => PersonalInstructionsBox.Clear();
    private async void VoiceSave_Click(object sender, RoutedEventArgs e)
    {
        SaveCloudVoiceSettings();
        _config.TtsEngine = TtsEngineBox.SelectedValue?.ToString() ?? "windows";
        _config.TtsVoiceName = TtsVoiceBox.SelectedValue?.ToString() ?? "";
        _config.TtsPiperVoiceId = TtsPiperVoiceBox.SelectedValue?.ToString() ?? _config.TtsPiperVoiceId;
        _config.TtsVolume = TtsVolumeSlider.Value / 100d;
        _tts.Engine = _config.TtsEngine; _tts.PreferredVoiceName = _config.TtsVoiceName;
        _tts.PiperVoiceId = _config.TtsPiperVoiceId; _tts.Volume = (float)_config.TtsVolume;
        try { await PersistConfig(); TtsVoiceStatus.Text = UiLocalization.Translate("Stimm-Einstellungen gespeichert.", _config.UiLanguage); }
        catch (Exception) { TtsVoiceStatus.Text = UiLocalization.Translate("Speichern fehlgeschlagen.", _config.UiLanguage); }
    }
    private void CloudVoice_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ElevenLabsVoiceBox.SelectedItem is CloudVoice voice && ElevenLabsVoiceIdBox is not null)
            ElevenLabsVoiceIdBox.Text = voice.Id;
    }
}
