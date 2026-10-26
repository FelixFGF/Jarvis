namespace Jarvis.Services;

public sealed record SpeechProviderAvailability(bool Available, string Message, IReadOnlyList<string> Languages);
public sealed record SpeechTranscriptionRequest(string AudioPath, string? WhisperModelPath, string LanguageTag,
    string? ElevenLabsApiKey = null, string? ElevenLabsModelId = null,
    string? GroqApiKey = null, string? GroqModelId = null,
    string? GoogleCredentialsJson = null, string? GoogleProjectId = null, string? GoogleModelId = null);
public interface ISpeechRecognitionProvider
{
    string Id { get; }
    SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request);
    Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken);
    Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken);
}
public sealed class SpeechRecognitionProviderRouter
{
    public const string Whisper="whisper", Windows="windows", ElevenLabs="elevenlabs", Groq="groq", Google="google";
    private readonly IReadOnlyDictionary<string, ISpeechRecognitionProvider> _providers;
    public SpeechRecognitionProviderRouter(IEnumerable<ISpeechRecognitionProvider> providers){_providers=providers.ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);if(!_providers.ContainsKey(Whisper))throw new ArgumentException("The Whisper provider is required.",nameof(providers));}
    public static string NormalizeProviderId(string? id)=>string.Equals(id,Windows,StringComparison.OrdinalIgnoreCase)?Windows:string.Equals(id,ElevenLabs,StringComparison.OrdinalIgnoreCase)?ElevenLabs:string.Equals(id,Groq,StringComparison.OrdinalIgnoreCase)?Groq:string.Equals(id,Google,StringComparison.OrdinalIgnoreCase)?Google:Whisper;
    public ISpeechRecognitionProvider Get(string? id)=>_providers[NormalizeProviderId(id)];
}
