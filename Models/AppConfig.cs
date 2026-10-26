using System.Collections.ObjectModel;

namespace Jarvis;

public sealed class AppConfig
{
    public ObservableCollection<ModelFallbackPlan> FallbackPlans { get; set; } = [];
    public string Endpoint { get; set; } = "http://localhost:1234/v1";
    public string Model { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public string ComputerUseFallbackEndpoint { get; set; } = "http://localhost:11434/v1";
    public string ComputerUseFallbackModel { get; set; } = "";
    public string ComputerUseFallbackApiKeyProtected { get; set; } = "";
    public bool ComputerUseFallbackCapabilitiesConfirmed { get; set; }
    public bool SaveChat { get; set; } = true;
    public bool RecentActivityTracking { get; set; }
    public bool ForegroundWindowMonitoring { get; set; }
    public string SystemPrompt { get; set; } = ""; // Legacy value retained for recovery; never used as protected instructions.
    public string PersonalInstructions { get; set; } = "";
    public ObservableCollection<string> AllowedRoots { get; set; } = [];
    public List<TrustedApprovalRecord> TrustedApprovals { get; set; } = [];
    public string ToolPermissionMode { get; set; } = "ask";
    public string SecurityReviewerEndpoint { get; set; } = "http://localhost:1234/v1";
    public string SecurityReviewerModel { get; set; } = "";
    public string SecurityReviewerApiKeyProtected { get; set; } = "";
    public string DisplayName { get; set; } = "FelixFGF";
    public string ProfileImagePath { get; set; } = "";
    public List<string> OpenTabIds { get; set; } = [];
    public string ActiveChatId { get; set; } = "";
    public List<string> RecentWorkspaces { get; set; } = [];
    public string WorkspaceRoot { get; set; } = "";
    public List<string> WorkspaceOpenFiles { get; set; } = [];
    public string WorkspaceActiveFile { get; set; } = "";
    public List<string> WorkspaceExpandedDirectories { get; set; } = [];
    public List<string> WorkspaceBrowserUrls { get; set; } = [];
    public string WorkspaceActiveView { get; set; } = "Files";
    public bool WorkspaceVisible { get; set; }
    public string WorkspaceLayoutMode { get; set; } = "Hidden";
    public bool ChatTerminalVisible { get; set; }
    public bool SpeechEnabled { get; set; } = true;
    public bool SpeechUseWindowsDefault { get; set; } = true;
    public string SpeechDeviceId { get; set; } = "";
    public string SpeechProvider { get; set; } = "whisper";
    public string SpeechModel { get; set; } = "small";
    public string ElevenLabsSttModelId { get; set; } = "scribe_v2";
    public string GroqSttApiKeyProtected { get; set; } = "";
    public string GroqSttModelId { get; set; } = "whisper-large-v3-turbo";
    public string GoogleSttCredentialsProtected { get; set; } = "";
    public string GoogleSttProjectId { get; set; } = "";
    public string GoogleSttModelId { get; set; } = "short";
    public string SpeechLanguage { get; set; } = "automatic";
    public int SpeechDefaultsVersion { get; set; } = 1;
    public bool WakeWordEnabled { get; set; }
    public string WakeWordPhrase { get; set; } = "Hey Jarvis";
    public double WakeSoundVolume { get; set; } = 0.8d;
    public int VoiceEndSilenceMs { get; set; } = 600;
    public string AlarmSound { get; set; } = "classic";
    public string TtsVoiceName { get; set; } = "";
    public string TtsEngine { get; set; } = "windows";
    public string TtsPiperVoiceId { get; set; } = "de_DE-thorsten-high";
    public string ElevenLabsApiKeyProtected { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "";
    public double TtsSpeed { get; set; } = 1d;
    public bool TtsAutoPlay { get; set; }
    public double TtsVolume { get; set; } = 1d;
    public string ResourceDirectory { get; set; } = "";
    public string UiLanguage { get; set; } = "";
    public string UpdateChannel { get; set; } = "";
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyRefreshTokenProtected { get; set; } = "";
    public string SpotifyAccount { get; set; } = "";
    public string AppleMusicDeveloperTokenProtected { get; set; } = "";
    public string AppleMusicUserTokenProtected { get; set; } = "";
    public string AppleMusicStorefront { get; set; } = "de";
}

public sealed record TrustedApprovalRecord(string Key, string Title, string Details)
{
    public string Label => $"{Title} · {Details}";
}
