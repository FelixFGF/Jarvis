using System.Windows;
using Jarvis.Services;

namespace Jarvis;
public partial class MainWindow
{
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
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(ElevenLabsKeyBox.Password)
                ? string.IsNullOrEmpty(_config.ElevenLabsApiKeyProtected) ? "" : UnprotectLocalSecret(_config.ElevenLabsApiKeyProtected)
                : ElevenLabsKeyBox.Password.Trim();
            using var cloud = new ElevenLabsTtsService();
            ElevenLabsVoiceBox.ItemsSource = await cloud.GetVoicesAsync(key, CancellationToken.None);
            TtsVoiceStatus.Text = UiLocalization.Translate("Cloud-Stimmen geladen.", _config.UiLanguage);
        }
        catch (Exception)
        {
            TtsVoiceStatus.Text = UiLocalization.Translate("Cloud-Stimmen nicht verfügbar. Prüfe Schlüssel, Verbindung und Kontolimit.", _config.UiLanguage);
        }
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
