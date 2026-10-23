using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Jarvis.Services;

public sealed record SpeechDevice(string Id, string Name, bool IsDefault);

/// <summary>Captures microphone input to a WAV file and transcribes it locally with Whisper.net.</summary>
public sealed class LocalSpeechService : IDisposable
{
    public static string StripLeadingWakePhrase(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(),
        @"^(?:(?:hey|hei|hay)\s+)?(?:jarvis|jervis|jarwis)(?=$|[\s,.:;!?\-–—])[\s,.:;!?\-–—]*", "",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _captureLock = new();
    private readonly SemaphoreSlim _modelLoadLock = new(1, 1);
    private WasapiCapture? _capture;
    private MMDevice? _captureDevice;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _recordingDone;
    private string? _recordingPath;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _loadedModel;
    private string? _loadedLanguage;
    private bool _disposed;
    private int _muted;

    public void SetMuted(bool muted) => Volatile.Write(ref _muted, muted ? 1 : 0);

    public IReadOnlyList<SpeechDevice> GetDevices()
    {
        string? defaultId = null;
        try { using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); defaultId = defaultDevice.ID; }
        catch (COMException) { }
        try
        {
            var result = new List<SpeechDevice>();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) result.Add(new SpeechDevice(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
            return result;
        }
        catch (COMException) { return []; }
    }

    public string GetDefaultDeviceName()
    {
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        return device.FriendlyName;
    }

    public void StartRecording(string? preferredDeviceId, bool useWindowsDefault, string outputPath, Action<float> levelChanged,
        byte[]? preRollAudio = null, WaveFormat? preRollFormat = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_captureLock)
        {
            if (_capture is not null) throw new InvalidOperationException("Eine Aufnahme läuft bereits.");
            var devices = GetDevices();
            if (devices.Count == 0) throw new InvalidOperationException("Windows hat kein aktives Mikrofon gefunden.");
            MMDevice device;
            if (useWindowsDefault) device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            else
            {
                var match = devices.FirstOrDefault(d => string.Equals(d.Id, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
                if (match is null) throw new InvalidOperationException("Das ausgewählte Mikrofon ist nicht verfügbar. Wähle in den Einstellungen ein aktives Gerät oder das Windows-Standardgerät.");
                device = _enumerator.GetDevice(match.Id);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            _recordingPath = outputPath;
            var deviceStored = false;
            try
            {
                var capture = new WasapiCapture(device);
                _captureDevice = device; deviceStored = true;
                _writer = new WaveFileWriter(outputPath, capture.WaveFormat);
                _recordingDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.DataAvailable += (_, e) =>
                {
                    var muted = Volatile.Read(ref _muted) != 0;
                    if (muted)
                    {
                        var silence = new byte[e.BytesRecorded];
                        if (capture.WaveFormat.Encoding == WaveFormatEncoding.Pcm && capture.WaveFormat.BitsPerSample == 8)
                            Array.Fill(silence, (byte)128);
                        lock (_captureLock) _writer?.Write(silence, 0, silence.Length);
                    }
                    else lock (_captureLock) _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                    var peak = muted ? 0f : MeasureLevel(e.Buffer, e.BytesRecorded, capture.WaveFormat);
                    try { levelChanged(peak); } catch { }
                };
                capture.RecordingStopped += (_, e) =>
                {
                    lock (_captureLock)
                    {
                        _writer?.Dispose(); _writer = null;
                        _capture?.Dispose(); _capture = null;
                        _captureDevice?.Dispose(); _captureDevice = null;
                        if (e.Exception is not null) _recordingDone?.TrySetException(e.Exception);
                        else if (!string.IsNullOrEmpty(_recordingPath)) _recordingDone?.TrySetResult(_recordingPath);
                        else _recordingDone?.TrySetCanceled();
                    }
                };
                _capture = capture;
                var preRollPeak = 0f;
                if (Volatile.Read(ref _muted) == 0 && preRollAudio is { Length: > 0 } && preRollFormat is not null
                    && SameWaveFormat(capture.WaveFormat, preRollFormat))
                {
                    var alignedCount = preRollAudio.Length - preRollAudio.Length % Math.Max(capture.WaveFormat.BlockAlign, 1);
                    if (alignedCount > 0)
                    {
                        _writer.Write(preRollAudio, 0, alignedCount);
                        preRollPeak = MeasureLevel(preRollAudio, alignedCount, preRollFormat);
                    }
                }
                capture.StartRecording();
                if (preRollPeak > 0) { try { levelChanged(preRollPeak); } catch { } }
            }
            catch
            {
                _writer?.Dispose(); _writer = null; _capture?.Dispose(); _capture = null;
                _captureDevice?.Dispose(); _captureDevice = null;
                if (!deviceStored) { try { device.Dispose(); } catch { } }
                throw;
            }
        }
    }

    private static bool SameWaveFormat(WaveFormat left, WaveFormat right) =>
        left.Encoding == right.Encoding && left.SampleRate == right.SampleRate && left.Channels == right.Channels
        && left.BitsPerSample == right.BitsPerSample && left.BlockAlign == right.BlockAlign;

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken)
    {
        Task<string> done;
        lock (_captureLock)
        {
            if (_capture is null || _recordingDone is null) throw new InvalidOperationException("Es läuft keine Aufnahme.");
            done = _recordingDone.Task;
            _capture.StopRecording();
        }
        return await done.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CancelRecording()
    {
        lock (_captureLock)
        {
            _recordingPath = null;
            if (_capture is not null) _capture.StopRecording();
            else _recordingDone?.TrySetCanceled();
        }
    }

    /// <summary>Starts a clean audio segment while keeping the active microphone capture running.</summary>
    public void ResetRecordingBuffer(string outputPath)
    {
        lock (_captureLock)
        {
            if (_capture is null || _writer is null) throw new InvalidOperationException("Es läuft keine Mikrofonaufnahme.");
            var format = _capture.WaveFormat;
            _writer.Dispose();
            _writer = null;
            if (File.Exists(outputPath)) File.Delete(outputPath);
            _writer = new WaveFileWriter(outputPath, format);
            _recordingPath = outputPath;
        }
    }

    public async Task WaitForRecordingStopAsync()
    {
        Task<string>? done;
        lock (_captureLock) done = _recordingDone?.Task;
        if (done is null) return;
        try { await done.ConfigureAwait(false); } catch { }
    }

    public async Task<string> TranscribeAsync(string wavPath, string modelPath, CancellationToken cancellationToken, string? language = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Das Whisper-Modell fehlt. Lade es zuerst in Einstellungen → Spracheingabe herunter.", modelPath);
        await EnsureProcessorAsync(modelPath, cancellationToken, language).ConfigureAwait(false);
        await using var source = new WaveFileReader(wavPath);
        if (source.TotalTime < TimeSpan.FromMilliseconds(350)) throw new InvalidDataException("Die Aufnahme war zu kurz. Sprich bitte etwas länger.");
        using var mono16k = ConvertToMono16k(source);
        var parts = new List<string>();
        await foreach (var segment in _processor!.ProcessAsync(mono16k, cancellationToken).ConfigureAwait(false))
            if (!string.IsNullOrWhiteSpace(segment.Text)) parts.Add(segment.Text.Trim());
        var result = string.Join(" ", parts).Trim();
        if (result.Length == 0) throw new InvalidDataException("Es wurde kein verständlicher Sprachtext erkannt. Prüfe das Mikrofon und sprich näher daran.");
        return result;
    }

    public static MemoryStream ConvertToMono16k(WaveFileReader source)
    {
        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        else if (samples.WaveFormat.Channels > 2) throw new InvalidDataException("Dieses Mikrofonformat mit mehr als zwei Kanälen wird nicht unterstützt.");
        if (samples.WaveFormat.SampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
        var mono16k = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(mono16k, samples.ToWaveProvider16());
        mono16k.Position = 0;
        return mono16k;
    }

    /// <summary>Loads the selected Whisper model before a voice turn so first-turn transcription stays responsive.</summary>
    public Task PreloadModelAsync(string modelPath, CancellationToken cancellationToken = default, string? language = null) => EnsureProcessorAsync(modelPath, cancellationToken, language);

    private async Task EnsureProcessorAsync(string modelPath, CancellationToken token, string? language = null)
    {
        language = string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();
        if (string.Equals(_loadedModel, modelPath, StringComparison.OrdinalIgnoreCase) && string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase) && _processor is not null) return;
        await _modelLoadLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (string.Equals(_loadedModel, modelPath, StringComparison.OrdinalIgnoreCase) && string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase) && _processor is not null) return;
            var loaded = await Task.Run(() =>
            {
                var factory = WhisperFactory.FromPath(modelPath);
                try
                {
                    var builder = factory.CreateBuilder()
                        // Voice turns are short and independent. Avoid carrying decoder
                        // context between them. Keep alternatives for short German utterances.
                        .WithNoContext()
                        .WithThreads(Math.Clamp(Environment.ProcessorCount, 2, 8));
                    ((BeamSearchSamplingStrategyBuilder)builder.WithBeamSearchSamplingStrategy()).WithBeamSize(5);
                    if (language is null) builder.WithLanguageDetection();
                    else builder.WithLanguage(language);
                    return (Factory: factory, Processor: builder.Build());
                }
                catch { factory.Dispose(); throw; }
            }, token).ConfigureAwait(false);
            var oldProcessor = _processor; var oldFactory = _factory;
            _factory = loaded.Factory; _processor = loaded.Processor; _loadedModel = modelPath; _loadedLanguage = language;
            oldProcessor?.Dispose(); oldFactory?.Dispose();
        }
        finally { _modelLoadLock.Release(); }
    }

    public static async Task DownloadModelAsync(string modelSize, string targetPath, CancellationToken token, IProgress<double>? progress = null)
    {
        var type = modelSize.ToLowerInvariant() switch
        {
            "tiny" => GgmlType.Tiny,
            "small" => GgmlType.Small,
            "medium" => GgmlType.Medium,
            "large-v3-turbo" => GgmlType.LargeV3Turbo,
            _ => GgmlType.Base
        };
        var quantization = QuantizationType.Q5_1;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var downloader = new WhisperGgmlDownloader(client);
        var modelStream = await downloader.GetGgmlModelAsync(type, quantization, token).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partial = targetPath + ".download";
        try
        {
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[128 * 1024]; long total = 0; int read;
                while ((read = await modelStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                { await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); total += read; progress?.Report(total); }
            }
            ValidateDownloadedModel(partial, modelSize);
            File.Move(partial, targetPath, true);
        }
        finally { modelStream.Dispose(); if (File.Exists(partial)) File.Delete(partial); }
    }

    public static void ValidateDownloadedModel(string path, string modelSize)
    {
        var minimumBytes = modelSize.ToLowerInvariant() switch
        {
            "tiny" => 20L * 1024 * 1024,
            "base" => 40L * 1024 * 1024,
            "small" => 120L * 1024 * 1024,
            "medium" => 350L * 1024 * 1024,
            "large-v3-turbo" => 350L * 1024 * 1024,
            _ => 20L * 1024 * 1024
        };

        var info = new FileInfo(path);
        if (!info.Exists || info.Length < minimumBytes)
            throw new InvalidDataException("The downloaded Whisper model is incomplete.");

        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];

        if (stream.Read(magic) != magic.Length ||
            !magic.SequenceEqual(new byte[] { 0x6C, 0x6D, 0x67, 0x67 }))
        {
            throw new InvalidDataException("The downloaded file is not a valid GGML Whisper model.");
        }
    }

    public static float MeasureLevel(byte[] bytes, int count, WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (isFloat && format.BitsPerSample == 32)
        {
            var max = 0f;
            for (var i = 0; i + 3 < count; i += 4) max = Math.Max(max, Math.Abs(BitConverter.ToSingle(bytes, i)));
            return Math.Clamp(max, 0f, 1f);
        }
        if (isFloat && format.BitsPerSample == 64)
        {
            var max = 0f;
            for (var i = 0; i + 7 < count; i += 8) max = Math.Max(max, (float)Math.Abs(BitConverter.ToDouble(bytes, i)));
            return Math.Clamp(max, 0f, 1f);
        }
        if (format.BitsPerSample == 8)
        {
            var max = 0f;
            for (var i = 0; i < count; i++) max = Math.Max(max, Math.Abs((bytes[i] - 128) / 128f));
            return Math.Clamp(max, 0f, 1f);
        }
        if (format.BitsPerSample == 16)
        {
            var max = 0f;
            for (var i = 0; i + 1 < count; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(bytes, i) / 32768f));
            return Math.Clamp(max, 0f, 1f);
        }
        if (format.BitsPerSample == 24)
        {
            var max = 0f;
            for (var i = 0; i + 2 < count; i += 3)
            {
                var sample = bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16);
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                max = Math.Max(max, Math.Abs(sample / 8388608f));
            }
            return Math.Clamp(max, 0f, 1f);
        }
        if (format.BitsPerSample == 32)
        {
            var max = 0f;
            for (var i = 0; i + 3 < count; i += 4) max = Math.Max(max, Math.Abs(BitConverter.ToInt32(bytes, i) / 2147483648f));
            return Math.Clamp(max, 0f, 1f);
        }
        return 0f;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        CancelRecording(); _processor?.Dispose(); _factory?.Dispose(); _enumerator.Dispose(); _modelLoadLock.Dispose();
    }
}
