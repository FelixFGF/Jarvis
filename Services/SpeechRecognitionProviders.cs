using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.Services;

public sealed record SpeechProviderAvailability(bool Available, string Message, IReadOnlyList<string> Languages);

public sealed record SpeechTranscriptionRequest(string AudioPath, string? WhisperModelPath, string LanguageTag);

public interface ISpeechRecognitionProvider
{
    string Id { get; }
    SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request);
    Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken);
    Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken);
}

public sealed class SpeechRecognitionProviderRouter
{
    public const string Whisper = "whisper";
    public const string Windows = "windows";
    private readonly IReadOnlyDictionary<string, ISpeechRecognitionProvider> _providers;

    public SpeechRecognitionProviderRouter(IEnumerable<ISpeechRecognitionProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        if (!_providers.ContainsKey(Whisper)) throw new ArgumentException("The Whisper provider is required.", nameof(providers));
    }

    public static string NormalizeProviderId(string? providerId) =>
        string.Equals(providerId, Windows, StringComparison.OrdinalIgnoreCase) ? Windows : Whisper;

    public ISpeechRecognitionProvider Get(string? providerId) => _providers[NormalizeProviderId(providerId)];
}

public sealed class WhisperSpeechRecognitionProvider(LocalSpeechService speech) : ISpeechRecognitionProvider
{
    public string Id => SpeechRecognitionProviderRouter.Whisper;

    public SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request)
    {
        var available = !string.IsNullOrWhiteSpace(request.WhisperModelPath) && File.Exists(request.WhisperModelPath);
        return new SpeechProviderAvailability(available,
            available ? "The selected Whisper model is installed and works locally." : "The selected Whisper model is not installed.",
            ["automatic", "de", "en"]);
    }

    public Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WhisperModelPath)) throw new InvalidOperationException("No Whisper model was selected.");
        return speech.PreloadModelAsync(request.WhisperModelPath, cancellationToken, WhisperLanguage(request.LanguageTag));
    }

    public Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WhisperModelPath)) throw new InvalidOperationException("No Whisper model was selected.");
        return speech.TranscribeAsync(request.AudioPath, request.WhisperModelPath, cancellationToken, WhisperLanguage(request.LanguageTag));
    }

    public static string? WhisperLanguage(string? tag) => string.IsNullOrWhiteSpace(tag) || tag == "automatic"
        ? null : CultureInfo.GetCultureInfo(tag).TwoLetterISOLanguageName;
}

/// <summary>
/// Uses an installed Windows desktop speech recognizer. Unlike Win+H and
/// Windows.Media.SpeechRecognition, this API works in Jarvis's unpackaged WPF build.
/// Recognition is performed by the installed desktop engine without an API key.
/// </summary>
public sealed class WindowsNativeSpeechRecognitionProvider : ISpeechRecognitionProvider
{
    public string Id => SpeechRecognitionProviderRouter.Windows;

    public SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request)
    {
        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var languages = recognizers.Select(info => info.Culture.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            var selected = FindRecognizer(recognizers, request.LanguageTag);
            if (selected is null)
                return new SpeechProviderAvailability(false,
                    languages.Length == 0
                        ? "No Windows desktop speech recognizer is installed. Add a Windows speech language component or select Whisper."
                        : $"No Windows speech recognizer matching {request.LanguageTag} is installed. Available: {string.Join(", ", languages)}.",
                    languages);
            return new SpeechProviderAvailability(true,
                $"Windows desktop speech recognizer ready: {selected.Description} ({selected.Culture.Name}). No Jarvis API key is required; network behavior depends on the installed Windows recognizer.",
                languages);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            return new SpeechProviderAvailability(false, "Windows native speech recognition is unavailable: " + ex.Message, []);
        }
    }

    public Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var availability = GetAvailability(request);
        if (!availability.Available) throw new InvalidOperationException(availability.Message);
        return Task.CompletedTask;
    }

    public async Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.AudioPath)) throw new FileNotFoundException("The recorded audio file is unavailable.", request.AudioPath);
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var recognizerInfo = FindRecognizer(recognizers, request.LanguageTag)
            ?? throw new InvalidOperationException(GetAvailability(request).Message);

        await using var source = new WaveFileReader(request.AudioPath);
        if (source.TotalTime < TimeSpan.FromMilliseconds(350)) throw new InvalidDataException("The recording was too short. Speak a little longer.");
        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        else if (samples.WaveFormat.Channels > 2) throw new InvalidDataException("The selected microphone format has more than two channels and is not supported.");
        if (samples.WaveFormat.SampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
        using var mono16k = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(mono16k, samples.ToWaveProvider16());
        mono16k.Position = 0;

        using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
        recognizer.LoadGrammar(new DictationGrammar());
        recognizer.SetInputToWaveStream(mono16k);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognized = new List<string>();
        recognizer.SpeechRecognized += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Result?.Text)) recognized.Add(args.Result.Text.Trim());
        };
        recognizer.RecognizeCompleted += (_, args) =>
        {
            if (args.Error is not null) completion.TrySetException(args.Error);
            else if (args.Cancelled) completion.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
            else completion.TrySetResult(string.Join(" ", recognized).Trim());
        };

        recognizer.RecognizeAsync(RecognizeMode.Multiple);
        using var cancellation = cancellationToken.Register(() =>
        {
            try { recognizer.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
        });
        var result = await completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Length == 0) throw new InvalidDataException("Windows did not recognize understandable speech. Check the selected language, microphone, and Windows speech components.");
        return result;
    }

    internal static RecognizerInfo? FindRecognizer(IEnumerable<RecognizerInfo> recognizers, string? languageTag)
    {
        var list = recognizers.ToArray();
        if (list.Length == 0) return null;
        CultureInfo requested;
        try { requested = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(languageTag) ? CultureInfo.CurrentUICulture.Name : languageTag); }
        catch (CultureNotFoundException) { requested = CultureInfo.CurrentUICulture; }
        return list.FirstOrDefault(info => string.Equals(info.Culture.Name, requested.Name, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(info => string.Equals(info.Culture.TwoLetterISOLanguageName, requested.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
    }
}
