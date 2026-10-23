using NAudio.Wave;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using Windows.Foundation;
using SherpaOnnx;
using System.Diagnostics;
using System.Threading.Channels;

namespace Jarvis.Services;

/// <summary>Offline Windows OneCore TTS with system-installed voices and local audio playback.</summary>
public sealed class LocalTtsService : IDisposable
{
    public static IReadOnlyList<PiperVoiceOption> PiperVoices { get; } =
    [
        new("de_DE-thorsten-high", "Thorsten · männlich · hohe Qualität", "de_DE-thorsten-high.onnx", 114, "Thorsten · male · high quality"),
        new("de_DE-eva_k-x_low", "Eva K. · weiblich · kompakt", "de_DE-eva_k-x_low.onnx", 40, "Eva K. · feminine · compact"),
        new("de_DE-ramona-low", "Ramona · weiblich · kompakt", "de_DE-ramona-low.onnx", 40, "Ramona · feminine · compact"),
        new("de_DE-kerstin-low", "Kerstin · weiblich · kompakt", "de_DE-kerstin-low.onnx", 40, "Kerstin · feminine · compact"),
        new("de_DE-karlsson-low", "Karlsson · männlich · kompakt", "de_DE-karlsson-low.onnx", 40, "Karlsson · masculine · compact"),
        new("de_DE-thorsten_emotional-medium", "Thorsten Emotional · männlich · ausdrucksstark", "de_DE-thorsten_emotional-medium.onnx", 80, "Thorsten Emotional · male · expressive"),
        new("en_US-lessac-medium", "Lessac · Englisch (USA) · neural", "en_US-lessac-medium.onnx", 75, "Lessac · English (US) · neural"),
        new("en_GB-alan-medium", "Alan · Englisch (UK) · neural", "en_GB-alan-medium.onnx", 70, "Alan · English (UK) · neural"),
    ];
    public static IReadOnlyList<PiperVoiceOption> PiperVoicesForLanguage(string? language) => language == "en"
        ? PiperVoices.Select(v => v with { Name = v.EnglishName }).ToArray()
        : PiperVoices;
    private readonly SpeechSynthesizer _synth = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private MemoryStream? _audio;
    private IAsyncOperation<SpeechSynthesisStream>? _operation;
    private TaskCompletionSource? _playbackFinished;
    private CancellationTokenSource? _speakCancellation;
    private string _preferredVoiceName = "";
    private int _generation;
    private OfflineTts? _piper;
    private string _piperModelDirectory = "";
    private string _engine = "windows";
    private string _piperVoiceId = "de_DE-thorsten-high";
    private float _volume = 1f;
    private LocalTtsState _state;
    private bool _disposed;
    public event EventHandler? StateChanged;
    public LocalTtsState State => _state;
    public IReadOnlyList<InstalledVoice> Voices => [new InstalledVoice("Automatisch · passende Sprache", ""), .. SpeechSynthesizer.AllVoices.Select(v => new InstalledVoice(v.DisplayName, v.Language))];
    public string PreferredVoiceName { get => _preferredVoiceName; set => _preferredVoiceName = value ?? ""; }
    public string Engine { get => _engine; set => _engine = value is "piper" or "elevenlabs" ? value : "windows"; }
    public string PiperVoiceId { get => _piperVoiceId; set => _piperVoiceId = PiperVoices.Any(v => v.Id == value) ? value : "de_DE-thorsten-high"; }
    public string PiperVoiceLanguage => _piperVoiceId.StartsWith("en_", StringComparison.OrdinalIgnoreCase) ? "en" : "de";
    private readonly ElevenLabsTtsService _cloud = new();
    public Func<string> CloudApiKey { get; set; } = () => "";
    public string CloudVoiceId { get; set; } = "";
    public double Speed { get; set; } = 1;
    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0f, 1f); if (_output is not null) _output.Volume = _volume; } }
    public bool IsPiperModelInstalled(string resourceDirectory, string? voiceId = null) => FindPiperModelDirectory(resourceDirectory, voiceId ?? _piperVoiceId) is not null;

    public async Task DownloadPiperModelAsync(string resourceDirectory, string voiceId, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var voice = PiperVoices.FirstOrDefault(v => v.Id == voiceId) ?? throw new ArgumentException("Unbekannte Neural-Stimme.", nameof(voiceId));
        var destination = Path.Combine(resourceDirectory, "Models", "TTS", "vits-piper-" + voice.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var archive = Path.Combine(Path.GetDirectoryName(destination)!, voice.Id + ".tar.bz2.part");
        var extractionRoot = Path.Combine(Path.GetDirectoryName(destination)!, voice.Id + "-extract");
        var archiveName = "vits-piper-" + voice.Id;
        var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{archiveName}.tar.bz2";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024]; long received = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                { await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); received += read; progress?.Report(total is > 0 ? received * 100 / total.Value : received); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var tar = FindTarExecutable();
            var listing = await RunTarAsync(tar, $"-tf {QuoteProcessArgument(archive)}", cancellationToken);
            if (listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(IsUnsafeArchiveEntry))
                throw new InvalidDataException("Das heruntergeladene Sprachmodell enthält ungültige Archivpfade.");
            if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, true);
            Directory.CreateDirectory(extractionRoot);
            await RunTarAsync(tar, $"-xf {QuoteProcessArgument(archive)} -C {QuoteProcessArgument(extractionRoot)}", cancellationToken);
            var model = Directory.GetFiles(extractionRoot, voice.ModelFile, SearchOption.AllDirectories).FirstOrDefault();
            if (model is null || !Directory.GetFiles(extractionRoot, "tokens.txt", SearchOption.AllDirectories).Any() || !Directory.GetDirectories(extractionRoot, "espeak-ng-data", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("Das Sprachmodell ist unvollständig; Modell, Token-Datei oder deutsche Sprachdaten fehlen.");
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(Path.GetDirectoryName(model)!, destination);
            Directory.Delete(extractionRoot, true);
            _piper?.Dispose(); _piper = null; _piperModelDirectory = "";
            // Validate and warm the downloaded voice immediately so it is usable on its first play.
            var validation = await Task.Run(() => GeneratePiper("Hallo, ich bin Jarvis.", resourceDirectory, voice), cancellationToken);
            if (validation.Bytes.Length == 0 || validation.SampleRate <= 0) throw new InvalidDataException("Das installierte Neural-Modell hat keine gültigen Audiodaten erzeugt.");
        }
        catch
        {
            _piper?.Dispose(); _piper = null; _piperModelDirectory = "";
            if (Directory.Exists(destination)) { try { Directory.Delete(destination, true); } catch { } }
            throw;
        }
        finally { try { if (File.Exists(archive)) File.Delete(archive); } catch { } try { if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, true); } catch { } }
    }

    public async Task SpeakAsync(string source, string? preferredLanguage = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = Prepare(source);
        if (text.Length == 0) throw new InvalidOperationException("Die Nachricht enthält keinen vorlesbaren Text.");
        if (text.Length > 20_000) text = text[..20_000];
        Stop();
        var generation = Interlocked.Increment(ref _generation);
        var cancellation = new CancellationTokenSource();
        _speakCancellation = cancellation;
        SetState(LocalTtsState.Generating);
        try
        {
            var culture = DetectCulture(text, preferredLanguage);
            var voices = SpeechSynthesizer.AllVoices;
            VoiceInformation? voice = null;
            var selectedPiper = PiperVoices.First(v => v.Id == _piperVoiceId);
            var usePiper = _engine == "piper";
            var useCloud = _engine == "elevenlabs";
            if (!usePiper && !useCloud)
            {
                voice = string.IsNullOrWhiteSpace(_preferredVoiceName) ? null : voices.FirstOrDefault(v => string.Equals(v.DisplayName, _preferredVoiceName, StringComparison.OrdinalIgnoreCase) && v.Language.StartsWith(culture, StringComparison.OrdinalIgnoreCase));
                voice ??= voices.Where(v => v.Language.StartsWith(culture, StringComparison.OrdinalIgnoreCase)).OrderByDescending(IsNaturalVoice).ThenByDescending(v => v.DisplayName.Contains("Online", StringComparison.OrdinalIgnoreCase)).ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
                if (voice is null) throw new InvalidOperationException("Keine lokale Windows-Sprachstimme ist installiert. Installiere eine Stimme unter Einstellungen → Zeit und Sprache → Sprachausgabe.");
                _synth.Voice = voice;
                _synth.Options.SpeakingRate = Math.Clamp(Speed, 0.7, 1.2);
            }
            foreach (var part in SplitForSpeech(text))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                byte[] bytes; WaveFormat format;
                if (useCloud)
                {
                    bytes = await _cloud.GenerateAsync(part, CloudVoiceId, Speed, CloudApiKey(), cancellation.Token);
                    format = new WaveFormat(24000, 16, 1);
                }
                else if (usePiper)
                {
                    var generated = await Task.Run(() => GeneratePiper(part, _resourceDirectory, selectedPiper), cancellation.Token);
                    bytes = generated.Bytes; format = new WaveFormat(generated.SampleRate, 16, 1);
                }
                else
                {
                    _operation = _synth.SynthesizeTextToStreamAsync(part);
                    using var stream = await _operation.AsTask(cancellation.Token);
                    _operation = null;
                    if (stream.Size == 0 || stream.Size > 80_000_000) throw new InvalidDataException("Windows hat eine leere oder unerwartet große Audiodatei erzeugt.");
                    using var dataReader = new DataReader(stream.GetInputStreamAt(0));
                    await dataReader.LoadAsync((uint)stream.Size).AsTask(cancellation.Token);
                    bytes = new byte[(int)stream.Size]; dataReader.ReadBytes(bytes); format = null!;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                _audio = new MemoryStream(bytes, writable: false);
                _reader = usePiper || useCloud ? new RawSourceWaveStream(_audio, format) : new WaveFileReader(_audio);
                _playbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _output = new WaveOutEvent { Volume = _volume }; _output.PlaybackStopped += PlaybackStopped;
                _output.Init(_reader); _output.Play(); SetState(LocalTtsState.Speaking);
                await _playbackFinished.Task.WaitAsync(cancellation.Token);
                CleanupPlayback();
            }
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) SetState(LocalTtsState.Ready); }
        catch (Exception ex) { if (generation == Volatile.Read(ref _generation)) { CleanupPlayback(); SetState(LocalTtsState.Ready); } throw new InvalidOperationException("Sprachausgabe fehlgeschlagen: " + ex.Message, ex); }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                if (ReferenceEquals(_speakCancellation, cancellation)) _speakCancellation = null;
                _operation = null; CleanupPlayback(); SetState(LocalTtsState.Ready);
            }
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Speaks complete sentence-sized chunks as soon as they arrive from a streaming
    /// chat response. The existing offline voice/model is reused; no sentence waits
    /// for the rest of the model response to finish.
    /// </summary>
    public async Task<bool> SpeakStreamAsync(ChannelReader<string> chunks, string? preferredLanguage, CancellationToken cancellationToken)
    {
        var pending = new StringBuilder();
        var spoke = false;
        await foreach (var chunk in chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(chunk)) continue;
            pending.Append(chunk);
            while (TryTakeSpeakableSentence(pending, out var sentence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SpeakAsync(sentence, preferredLanguage).ConfigureAwait(false);
                spoke = true;
            }
        }

        var remainder = pending.ToString().Trim();
        if (remainder.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SpeakAsync(remainder, preferredLanguage).ConfigureAwait(false);
            spoke = true;
        }
        return spoke;
    }

    private static bool TryTakeSpeakableSentence(StringBuilder pending, out string sentence)
    {
        sentence = "";
        // Do not split at decimal points or common abbreviations. A short sentence
        // is a good latency/intonation compromise for conversational voice replies.
        for (var i = 0; i < pending.Length; i++)
        {
            if (pending[i] is not ('.' or '!' or '?' or '\n')) continue;
            if (pending[i] == '.' && i > 0 && i + 1 < pending.Length && char.IsDigit(pending[i - 1]) && char.IsDigit(pending[i + 1])) continue;
            if (pending[i] is '.' or '!' or '?' && i + 1 < pending.Length && !char.IsWhiteSpace(pending[i + 1])) continue;
            var end = i + 1;
            sentence = pending.ToString(0, end).Trim();
            pending.Remove(0, end);
            while (pending.Length > 0 && char.IsWhiteSpace(pending[0])) pending.Remove(0, 1);
            return sentence.Length > 0;
        }
        // Some local models emit long unpunctuated replies. Begin speaking a
        // bounded clause as soon as it is usable instead of waiting for the
        // model's final punctuation or the complete response.
        const int maxPendingChars = 110;
        if (pending.Length >= maxPendingChars)
        {
            var split = pending.ToString(0, maxPendingChars).LastIndexOf(' ');
            if (split >= 40)
            {
                sentence = pending.ToString(0, split).Trim();
                pending.Remove(0, split + 1);
                while (pending.Length > 0 && char.IsWhiteSpace(pending[0])) pending.Remove(0, 1);
                return sentence.Length > 0;
            }
        }
        return false;
    }

    /// <summary>Loads the selected local Piper voice ahead of a conversation so the first reply does not pay model startup cost.</summary>
    public async Task PreloadSelectedVoiceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _engine != "piper") return;
        var voice = PiperVoices.First(v => v.Id == _piperVoiceId);
        if (FindPiperModelDirectory(_resourceDirectory, voice.Id) is null) return;
        await Task.Run(() => GeneratePiper("Hello, I am Jarvis.", _resourceDirectory, voice), cancellationToken).ConfigureAwait(false);
    }

    public void Pause() { if (_state == LocalTtsState.Speaking) { _output?.Pause(); SetState(LocalTtsState.Paused); } }
    public void Resume() { if (_state == LocalTtsState.Paused) { _output?.Play(); SetState(LocalTtsState.Speaking); } }
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        try { _speakCancellation?.Cancel(); } catch { }
        try { _operation?.Cancel(); } catch { }
        _operation = null;
        _playbackFinished?.TrySetCanceled(); _playbackFinished = null;
        if (_output is not null) { _output.PlaybackStopped -= PlaybackStopped; try { _output.Stop(); } catch { } }
        CleanupPlayback(); SetState(LocalTtsState.Ready);
    }
    public void RefreshState()
    {
        // SpeakAsync owns the state across all sentence chunks. The UI timer must not
        // report Ready between chunks or Voice Mode can reopen the microphone mid-reply.
        if (_speakCancellation is null && _output?.PlaybackState == PlaybackState.Stopped)
        { CleanupPlayback(); SetState(LocalTtsState.Ready); }
    }
    private void PlaybackStopped(object? sender, StoppedEventArgs e) => _playbackFinished?.TrySetResult();
    private void CleanupPlayback()
    {
        try { _output?.Dispose(); } catch { } _output = null;
        try { _reader?.Dispose(); } catch { } _reader = null;
        try { _audio?.Dispose(); } catch { } _audio = null;
        _playbackFinished = null;
    }
    private static bool IsNaturalVoice(VoiceInformation voice) => voice.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) || voice.DisplayName.Contains("Neural", StringComparison.OrdinalIgnoreCase);
    private string _resourceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");
    public string ResourceDirectory { get => _resourceDirectory; set => _resourceDirectory = string.IsNullOrWhiteSpace(value) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis") : value; }
    private (byte[] Bytes, int SampleRate) GeneratePiper(string text, string resourceDirectory, PiperVoiceOption voice)
    {
        var modelDirectory = FindPiperModelDirectory(resourceDirectory, voice.Id) ?? throw new FileNotFoundException("Die ausgewählte lokale Neural-Stimme fehlt. Lade sie unter Einstellungen → Sprachausgabe herunter.");
        if (_piper is null || !string.Equals(_piperModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _piper?.Dispose();
            var model = Directory.GetFiles(modelDirectory, voice.ModelFile, SearchOption.AllDirectories).FirstOrDefault() ?? throw new FileNotFoundException("Modell-Datei " + voice.ModelFile + " fehlt.");
            var tokens = Directory.GetFiles(modelDirectory, "tokens.txt", SearchOption.AllDirectories).FirstOrDefault() ?? throw new FileNotFoundException("Piper tokens.txt fehlt.");
            var dataDir = Directory.GetDirectories(modelDirectory, "espeak-ng-data", SearchOption.AllDirectories).FirstOrDefault() ?? throw new DirectoryNotFoundException("Piper espeak-ng-data fehlt.");
            var config = new OfflineTtsConfig(); config.Model.Vits.Model = model; config.Model.Vits.Tokens = tokens; config.Model.Vits.DataDir = dataDir; config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4); config.Model.Provider = "cpu"; config.MaxNumSentences = 1;
            _piper = new OfflineTts(config); _piperModelDirectory = modelDirectory;
        }
        var generation = new OfflineTtsGenerationConfig { Sid = 0, Speed = (float)Math.Clamp(Speed, 0.7, 1.2), SilenceScale = 0.2f };
        var audio = _piper.GenerateWithConfig(text, generation, null);
        if (audio.Samples.Length == 0) throw new InvalidDataException("Das Neural-Modell hat kein Audio erzeugt.");
        var pcm = new short[audio.Samples.Length];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)Math.Round(Math.Clamp(audio.Samples[i], -1f, 1f) * short.MaxValue), short.MinValue, short.MaxValue);
        var bytes = new byte[pcm.Length * sizeof(short)]; System.Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return (bytes, audio.SampleRate);
    }
    private static string? FindPiperModelDirectory(string resourceDirectory, string voiceId)
    {
        if (!Directory.Exists(resourceDirectory)) return null;
        var voice = PiperVoices.FirstOrDefault(v => v.Id == voiceId);
        return voice is not null && Directory.GetFiles(resourceDirectory, voice.ModelFile, SearchOption.AllDirectories).FirstOrDefault() is { } model ? Path.GetDirectoryName(model) : null;
    }
    private static string FindTarExecutable()
    {
        var systemTar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        return File.Exists(systemTar) ? systemTar : throw new FileNotFoundException("Windows tar.exe fehlt. Für das lokale TTS-Modell wird das mit Windows gelieferte Archivwerkzeug benötigt.");
    }
    private static string QuoteProcessArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static bool IsUnsafeArchiveEntry(string entry)
    {
        var normalized = entry.Replace('\\', '/');
        return Path.IsPathRooted(entry) || normalized.StartsWith('/') || normalized.Split('/').Any(segment => segment == "..");
    }
    private static async Task<string> RunTarAsync(string executable, string arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        if (!process.Start()) throw new InvalidOperationException("Windows konnte tar.exe nicht starten.");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("Das TTS-Archiv konnte nicht verarbeitet werden: " + error);
        return output;
    }
    private static IEnumerable<string> SplitForSpeech(string text)
    {
        // Keep the first synthesis request short so playback starts quickly. Long
        // single sentences are split at word boundaries rather than sent as one slow job.
        const int maxChars = 180;
        var sentences = Regex.Split(text, @"(?<=[.!?。！？])\s+");
        var buffer = new StringBuilder();
        foreach (var sentence in sentences)
        {
            var piece = sentence.Trim();
            if (piece.Length == 0) continue;
            if (piece.Length <= maxChars)
            {
                if (buffer.Length > 0 && buffer.Length + piece.Length + 1 > maxChars)
                { yield return buffer.ToString(); buffer.Clear(); }
                if (buffer.Length > 0) buffer.Append(' ');
                buffer.Append(piece);
                continue;
            }

            if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
            var longPart = new StringBuilder();
            foreach (var word in Regex.Split(piece, @"\s+"))
            {
                if (word.Length == 0) continue;
                if (longPart.Length > 0 && longPart.Length + word.Length + 1 > maxChars)
                { yield return longPart.ToString(); longPart.Clear(); }
                if (longPart.Length > 0) longPart.Append(' ');
                if (word.Length <= maxChars) longPart.Append(word);
                else
                {
                    if (longPart.Length > 0) { yield return longPart.ToString(); longPart.Clear(); }
                    for (var offset = 0; offset < word.Length; offset += maxChars)
                        yield return word.Substring(offset, Math.Min(maxChars, word.Length - offset));
                }
            }
            if (longPart.Length > 0) yield return longPart.ToString();
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }
    private void SetState(LocalTtsState state) { if (_state == state) return; _state = state; StateChanged?.Invoke(this, EventArgs.Empty); }

    public static string Prepare(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var text = Regex.Replace(source, "```(?:[\\w+#.-]+)?\\s*(.*?)```", " Codeblock: $1. ", RegexOptions.Singleline);
        text = Regex.Replace(text, "`([^`]+)`", "$1");
        text = Regex.Replace(text, "!?\\[([^\\]]*)\\]\\((https?://[^)]+)\\)", "$1: $2");
        text = Regex.Replace(text, "^\\s{0,3}#{1,6}\\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "^\\s*>\\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "^\\s*[-*+]\\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "^\\s*\\d+[.)]\\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "\\|", " "); text = Regex.Replace(text, "[*_~]{1,3}", ""); text = Regex.Replace(text, "<[^>]+>", " ");
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is 0x200D or 0xFE0E or 0xFE0F || value is >= 0x1F000 and <= 0x1FAFF || value is >= 0x2600 and <= 0x27BF || value is >= 0x1F1E6 and <= 0x1F1FF) continue;
            builder.Append(rune.ToString());
        }
        return Regex.Replace(builder.ToString(), @"[ \t]+", " ").Replace("\r", "").Trim();
    }

    public static string DetectCulture(string text, string? preferredLanguage = null)
    {
        var german = Regex.Matches(text, @"\b(der|die|das|und|ist|nicht|mit|für|ein|eine|ich|du|was|wie|bitte|danke)\b", RegexOptions.IgnoreCase).Count;
        var english = Regex.Matches(text, @"\b(the|and|is|not|with|for|a|an|I|you|what|how|please|thanks)\b", RegexOptions.IgnoreCase).Count;
        if (Math.Abs(german - english) <= 1)
        {
            if (preferredLanguage?.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true) return "de";
            if (preferredLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true) return "en";
        }
        return german >= english ? "de" : "en";
    }
    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; _synth.Dispose(); _cloud.Dispose(); _piper?.Dispose(); _piper = null; }
}

public enum LocalTtsState { Ready, Generating, Speaking, Paused }
public sealed record InstalledVoice(string Name, string Culture);
public sealed record PiperVoiceOption(string Id, string Name, string ModelFile, int ApproximateMegabytes, string EnglishName);
