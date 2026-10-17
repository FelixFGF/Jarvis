using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;

namespace Jarvis;

public partial class VoiceModeWindow : Window
{
    public event EventHandler? RetryRequested;
    public event EventHandler? EndRequested;
    public event Action<bool>? MuteChanged;
    private string _language = "en";
    private VoiceUiState _currentState = VoiceUiState.Ready;
    private string _currentTitle = "Ready when you are";
    private string _currentDetail = "Speak naturally. Jarvis will listen for a short pause.";

    public VoiceModeWindow()
    {
        InitializeComponent();
        SetState(VoiceUiState.Ready, "Ready when you are", "Speak naturally. Jarvis will listen for a short pause.");
    }

    public void SetTranscript(string text) => TranscriptText.Text = text;
    public void SetResponse(string text) => ResponseText.Text = text;

    public void SetLanguage(string language)
    {
        _language = language == "de" ? "de" : "en";
        Title = _language == "de" ? "Jarvis Sprache" : "Jarvis Voice";
        Subtitle.Text = _language == "de" ? "SPRACHMODUS" : "VOICE MODE";
        TranscriptLabel.Text = _language == "de" ? "DU" : "YOU";
        RetryButton.Content = _language == "de" ? "Erneut versuchen" : "Try again";
        EndButton.Content = _language == "de" ? "Sprachmodus beenden" : "End voice mode";
        EndButton.ToolTip = _language == "de" ? "Sprachmodus beenden" : "End voice mode";
        MuteButton.Content = MuteButton.IsChecked == true
            ? (_language == "de" ? "Mikrofon einschalten" : "Unmute microphone")
            : (_language == "de" ? "Mikrofon stummschalten" : "Mute microphone");
        MuteButton.ToolTip = MuteButton.Content.ToString();
        SetState(_currentState, _currentTitle, _currentDetail);
    }

    public void SetState(VoiceUiState state, string title, string detail)
    {
        _currentState = state; _currentTitle = title; _currentDetail = detail;
        StateText.Text = _language == "de" ? title switch
        {
            "Ready when you are" => "Bereit, wenn du es bist", "Listening…" => "Jarvis hört zu…", "Thinking…" => "Jarvis denkt nach…",
            "Preparing speech recognition…" => "Spracherkennung wird vorbereitet…",
            "Understanding…" => "Verarbeitet deine Sprache…", "Jarvis is speaking…" => "Jarvis spricht…", "Voice playback failed" => "Sprachausgabe fehlgeschlagen",
            "Voice mode could not start" => "Sprachmodus konnte nicht gestartet werden",
            "Speech recognition failed" => "Spracherkennung fehlgeschlagen", "Microphone or speech model unavailable" => "Mikrofon oder Sprachmodell nicht verfügbar",
            "No AI model selected" => "Kein KI-Modell ausgewählt", "Jarvis could not complete that request" => "Jarvis konnte die Anfrage nicht abschließen", _ => title
        } : title;
        DetailText.Text = _language == "de" ? detail switch
        {
            "Warming up your local speech model for faster replies." => "Das lokale Sprachmodell wird für schnellere Antworten vorbereitet.",
            "Voice components could not be prepared. Check Speech input and Speech output in Settings." => "Die Sprachfunktionen konnten nicht vorbereitet werden. Prüfe Spracheingabe und Sprachausgabe in den Einstellungen.",
            "Speak naturally. Jarvis will listen for a short pause." => "Sprich ganz normal. Jarvis reagiert nach einer kurzen Sprechpause.",
            "Converting your speech locally." => "Deine Sprache wird lokal in Text umgewandelt.",
            "Jarvis will listen again when the response finishes." => "Wenn die Antwort fertig ist, hört Jarvis wieder zu.",
            "Speak naturally. Jarvis will respond after a short pause." => "Sprich ganz normal. Nach einer kurzen Pause antwortet Jarvis.", _ => detail
        } : detail;
        RetryButton.Visibility = state == VoiceUiState.Error ? Visibility.Visible : Visibility.Collapsed;
        var active = state is VoiceUiState.Listening or VoiceUiState.Speaking;
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
        LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
        if (active)
        {
            var scale = new DoubleAnimation(0.96, 1.045, TimeSpan.FromMilliseconds(state == VoiceUiState.Speaking ? 430 : 760)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
            LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale.Clone());
            LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(14, state == VoiceUiState.Speaking ? 34 : 24, TimeSpan.FromMilliseconds(680)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(0.32, state == VoiceUiState.Speaking ? 0.72 : 0.5, TimeSpan.FromMilliseconds(680)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            LogoScale.ScaleX = LogoScale.ScaleY = 1;
            LogoGlow.BlurRadius = 13;
            LogoGlow.Opacity = state == VoiceUiState.Error ? 0.1 : 0.34;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);
    private void MuteButton_Checked(object sender, RoutedEventArgs e)
    {
        MuteButton.Content = _language == "de" ? "Mikrofon einschalten" : "Unmute microphone";
        MuteButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(61, 45, 102));
        MuteChanged?.Invoke(true);
    }
    private void MuteButton_Unchecked(object sender, RoutedEventArgs e)
    {
        MuteButton.Content = _language == "de" ? "Mikrofon stummschalten" : "Mute microphone";
        MuteButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 31, 44));
        MuteChanged?.Invoke(false);
    }
    private void Close_Click(object sender, RoutedEventArgs e) { EndRequested?.Invoke(this, EventArgs.Empty); if (IsVisible) Close(); }
}

public enum VoiceUiState { Ready, Listening, Thinking, Speaking, Error }
