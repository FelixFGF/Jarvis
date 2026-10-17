using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Jarvis.Services;

/// <summary>Local, continuously running keyword spotter for the Jarvis wake phrase.</summary>
public sealed class LocalWakeWordService : IDisposable
{
    private const int SampleRate = 16_000;
    private const string ModelArchiveName = "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01";
    private const float WakeKeywordBoost = 4.0f;
    private const float WakeKeywordThreshold = 0.10f;
    private const string ModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01.tar.bz2";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly MMDeviceEnumerator _devices = new();
    private bool _disposed;

    public async Task ListenAsync(string? deviceId, bool useDefault, string phrase, string resourceDirectory, CancellationToken cancellationToken,
        Action<string, byte[], WaveFormat> onDetected, IProgress<string>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var modelDirectory = await EnsureModelAsync(resourceDirectory, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var encoder = Path.Combine(modelDirectory, "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx");
        var decoder = Path.Combine(modelDirectory, "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx");
        var joiner = Path.Combine(modelDirectory, "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx");
        var tokens = Path.Combine(modelDirectory, "tokens.txt");
        var bpeModel = Path.Combine(modelDirectory, "bpe.model");
        var keywords = Path.Combine(modelDirectory, "jarvis-keywords.txt");
        foreach (var path in new[] { encoder, decoder, joiner, tokens, bpeModel })
            if (!File.Exists(path)) throw new FileNotFoundException("The local wake-word model is incomplete.", path);
        var normalizedPhrase = string.Join(' ', phrase.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalizedPhrase.Length is < 3 or > 80) normalizedPhrase = "HEY JARVIS";
        var phrases = new List<(string Phrase, float Boost, float Threshold)> { (normalizedPhrase, WakeKeywordBoost, WakeKeywordThreshold) };
        // The English-only model can confuse Jarvis with the acoustically similar
        // "Chavez" spelling for some accents. Keep these aliases limited to the
        // built-in wake phrase; each still requires the full two-word phrase.
        if (normalizedPhrase.Equals("HEY JARVIS", StringComparison.Ordinal))
        {
            phrases.Add(("HEY CHAVEZ", 2.5f, 0.14f));
            phrases.Add(("HEY CHAVIS", 2.5f, 0.14f));
        }
        // Sherpa's keyword file accepts one encoded BPE phrase per line, with a
        // boosting score and acoustic trigger threshold on each entry.
        var keywordLines = phrases.Select(item =>
            $"{EncodeBpeKeyword(item.Phrase, tokens, bpeModel)} :{item.Boost:0.0} #{item.Threshold:0.00}");
        await File.WriteAllTextAsync(keywords, string.Join(Environment.NewLine, keywordLines) + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        progress?.Report("Loading local wake-word detector…");
        using var spotter = CreateSpotter(encoder, decoder, joiner, tokens, keywords);
        using var stream = spotter.CreateStream();
        using var device = useDefault
            ? _devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            : GetSelectedDevice(deviceId);

        using var capture = new WasapiCapture(device);
        var buffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            // Do not let a slow inference loop build seconds of stale microphone audio.
            // A bounded 400 ms queue keeps the detector close to live speech.
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        var preRoll = new AudioPreRollBuffer(capture.WaveFormat, TimeSpan.FromMilliseconds(1600));
        capture.DataAvailable += (_, args) =>
        {
            try
            {
                buffered.AddSamples(args.Buffer, 0, args.BytesRecorded);
                preRoll.Add(args.Buffer, args.BytesRecorded);
            }
            catch (InvalidOperationException) { /* Capture may be stopping while the device is unplugged. */ }
        };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null) stopped.TrySetException(args.Exception);
            else stopped.TrySetResult();
        };

        ISampleProvider samples = buffered.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        else if (samples.WaveFormat.Channels != 1) throw new InvalidOperationException("The selected microphone must provide one or two audio channels.");
        if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);

        capture.StartRecording();
        progress?.Report("Listening continuously for “" + normalizedPhrase + "”…");
        try
        {
            var block = new float[1600]; // 100 ms at 16 kHz; Sherpa decodes incrementally.
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = samples.Read(block, 0, block.Length);
                if (count == 0)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var accepted = count == block.Length ? block : block[..count];
                stream.AcceptWaveform(SampleRate, accepted);
                while (spotter.IsReady(stream))
                {
                    spotter.Decode(stream);
                    var keyword = spotter.GetResult(stream).Keyword;
                    if (string.IsNullOrWhiteSpace(keyword)) continue;
                    spotter.Reset(stream);
                    onDetected(keyword, preRoll.Snapshot(), capture.WaveFormat);
                    return;
                }
            }
        }
        finally
        {
            try { capture.StopRecording(); } catch { }
            try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
    }

    private static KeywordSpotter CreateSpotter(string encoder, string decoder, string joiner, string tokens, string keywords)
    {
        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 1;
        config.KeywordsFile = keywords;
        config.KeywordsScore = WakeKeywordBoost;
        config.KeywordsThreshold = WakeKeywordThreshold;
        return new KeywordSpotter(config);
    }

    private static string EncodeBpeKeyword(string phrase, string tokensPath, string bpeModelPath)
    {
        // Parse only the SentencePiece vocabulary and scores needed for unigram
        // Viterbi segmentation. Microsoft.ML.Tokenizers currently throws an
        // IndexOutOfRangeException loading Sherpa's official bpe.model, so use
        // the model data directly instead of letting an optional UI feature fail.
        var scores = ReadSentencePieceScores(bpeModelPath);
        var normalized = NormalizeSentencePieceInput(phrase);
        var trie = new SentencePieceTrie();
        foreach (var (piece, score) in scores) trie.Add(piece, score);
        var encoded = SegmentSentencePiece(normalized, trie);
        var tokenVocabulary = File.ReadLines(tokensPath)
            .Select(line => line.TrimEnd().LastIndexOf(' ') is var separator && separator > 0 ? line.TrimEnd()[..separator] : line.TrimEnd())
            .ToHashSet(StringComparer.Ordinal);
        if (encoded.Length == 0 || encoded.Any(piece => !tokenVocabulary.Contains(piece)))
            throw new InvalidDataException($"The SentencePiece tokenizer could not encode wake phrase '{phrase}' using this Sherpa model's vocabulary.");
        return string.Join(' ', encoded);
    }

    private static string NormalizeSentencePieceInput(string phrase)
    {
        var source = phrase.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var result = new StringBuilder("▁");
        var pendingSpace = false;
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { pendingSpace = result.Length > 1; continue; }
            if (pendingSpace) result.Append('▁');
            result.Append(rune.ToString());
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static string[] SegmentSentencePiece(string text, SentencePieceTrie trie)
    {
        var best = Enumerable.Repeat(float.NegativeInfinity, text.Length + 1).ToArray();
        var previous = new int[text.Length + 1];
        var selected = new string?[text.Length + 1];
        best[0] = 0;
        for (var start = 0; start < text.Length; start++)
        {
            if (float.IsNegativeInfinity(best[start])) continue;
            var node = trie.Root;
            for (var end = start; end < text.Length; end++)
            {
                if (!node.Children.TryGetValue(text[end], out node!)) break;
                if (node.Piece is null) continue;
                var candidate = best[start] + node.Score;
                if (candidate > best[end + 1] || (candidate == best[end + 1] && end + 1 - start > end + 1 - previous[end + 1]))
                {
                    best[end + 1] = candidate;
                    previous[end + 1] = start;
                    selected[end + 1] = node.Piece;
                }
            }
        }

        if (float.IsNegativeInfinity(best[text.Length]))
            throw new InvalidDataException("The wake phrase contains characters unsupported by the local English wake-word model. Use an English phrase such as ‘Hey Jarvis’.");
        var result = new List<string>();
        for (var position = text.Length; position > 0; position = previous[position])
        {
            result.Add(selected[position] ?? throw new InvalidDataException("The wake phrase could not be segmented by the local model."));
            if (previous[position] >= position) throw new InvalidDataException("The wake phrase tokenizer could not make progress.");
        }
        result.Reverse();
        return result.ToArray();
    }

    private static Dictionary<string, float> ReadSentencePieceScores(string modelPath)
    {
        using var reader = new BinaryReader(File.OpenRead(modelPath), Encoding.UTF8, leaveOpen: false);
        var pieces = new Dictionary<string, float>(StringComparer.Ordinal);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var tag = ReadProtoVarint(reader);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (field == 1 && wire == 2)
            {
                var size = checked((int)ReadProtoVarint(reader));
                var bytes = reader.ReadBytes(size);
                if (bytes.Length != size) throw new EndOfStreamException("The SentencePiece model is truncated.");
                ReadSentencePieceEntry(bytes, pieces);
            }
            else SkipProtoField(reader, wire);
        }
        if (pieces.Count == 0) throw new InvalidDataException("The SentencePiece wake-word vocabulary is empty.");
        return pieces;
    }

    private static void ReadSentencePieceEntry(byte[] data, Dictionary<string, float> pieces)
    {
        using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        string? piece = null;
        float score = 0;
        var type = 1;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var tag = ReadProtoVarint(reader);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (field == 1 && wire == 2)
            {
                var size = checked((int)ReadProtoVarint(reader));
                var bytes = reader.ReadBytes(size);
                if (bytes.Length != size) throw new EndOfStreamException("The SentencePiece vocabulary entry is truncated.");
                piece = Encoding.UTF8.GetString(bytes);
            }
            else if (field == 2 && wire == 5) score = BitConverter.ToSingle(reader.ReadBytes(4));
            else if (field == 3 && wire == 0) type = checked((int)ReadProtoVarint(reader));
            else SkipProtoField(reader, wire);
        }
        // NORMAL=1 and USER_DEFINED=4 are the only ordinary text pieces.
        if (!string.IsNullOrEmpty(piece) && type is 1 or 4) pieces[piece] = score;
    }

    private static ulong ReadProtoVarint(BinaryReader reader)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var current = reader.ReadByte();
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException("The SentencePiece model contains an invalid protobuf value.");
    }

    private static void SkipProtoField(BinaryReader reader, int wire)
    {
        switch (wire)
        {
            case 0: _ = ReadProtoVarint(reader); break;
            case 1: reader.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 2: reader.BaseStream.Seek(checked((long)ReadProtoVarint(reader)), SeekOrigin.Current); break;
            case 5: reader.BaseStream.Seek(4, SeekOrigin.Current); break;
            default: throw new InvalidDataException("The SentencePiece model uses an unsupported protobuf field.");
        }
        if (reader.BaseStream.Position > reader.BaseStream.Length) throw new EndOfStreamException("The SentencePiece model is truncated.");
    }

    private sealed class SentencePieceTrie
    {
        public SentencePieceTrieNode Root { get; } = new();
        public void Add(string piece, float score)
        {
            var node = Root;
            foreach (var character in piece)
            {
                if (!node.Children.TryGetValue(character, out var child)) node.Children[character] = child = new SentencePieceTrieNode();
                node = child;
            }
            node.Piece = piece;
            node.Score = score;
        }
    }

    private sealed class SentencePieceTrieNode
    {
        public Dictionary<char, SentencePieceTrieNode> Children { get; } = new();
        public string? Piece { get; set; }
        public float Score { get; set; }
    }

    private sealed class AudioPreRollBuffer
    {
        private readonly object _sync = new();
        private readonly byte[] _buffer;
        private int _writePosition;
        private int _count;

        public AudioPreRollBuffer(WaveFormat format, TimeSpan duration)
        {
            Format = format;
            var requestedBytes = checked((int)(format.AverageBytesPerSecond * duration.TotalSeconds));
            var alignedBytes = requestedBytes - requestedBytes % Math.Max(format.BlockAlign, 1);
            _buffer = new byte[Math.Max(alignedBytes, format.BlockAlign)];
        }

        public WaveFormat Format { get; }

        public void Add(byte[] source, int count)
        {
            lock (_sync)
            {
                if (count >= _buffer.Length)
                {
                    var offset = count - _buffer.Length;
                    offset -= offset % Math.Max(Format.BlockAlign, 1);
                    Buffer.BlockCopy(source, offset, _buffer, 0, _buffer.Length);
                    _writePosition = 0;
                    _count = _buffer.Length;
                    return;
                }
                var firstCount = Math.Min(count, _buffer.Length - _writePosition);
                Buffer.BlockCopy(source, 0, _buffer, _writePosition, firstCount);
                var remainder = count - firstCount;
                if (remainder > 0) Buffer.BlockCopy(source, firstCount, _buffer, 0, remainder);
                _writePosition = (_writePosition + count) % _buffer.Length;
                _count = Math.Min(_buffer.Length, _count + count);
            }
        }

        public byte[] Snapshot()
        {
            lock (_sync)
            {
                var result = new byte[_count];
                var start = (_writePosition - _count + _buffer.Length) % _buffer.Length;
                var firstCount = Math.Min(_count, _buffer.Length - start);
                Buffer.BlockCopy(_buffer, start, result, 0, firstCount);
                if (firstCount < _count) Buffer.BlockCopy(_buffer, 0, result, firstCount, _count - firstCount);
                return result;
            }
        }
    }

    private MMDevice GetSelectedDevice(string? id)
    {
        var devices = _devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        try
        {
            var selected = devices.FirstOrDefault(device => string.Equals(device.ID, id, StringComparison.OrdinalIgnoreCase));
            if (selected is null) throw new InvalidOperationException("The selected microphone is no longer available. Choose another microphone or Windows default in Speech input settings.");
            var selectedId = selected.ID;
            return _devices.GetDevice(selectedId);
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }

    public static bool IsModelInstalled(string resourceDirectory)
    {
        var modelDirectory = Path.Combine(resourceDirectory, "Models", "Speech", "wake-word", ModelArchiveName);
        return new[]
        {
            "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "tokens.txt",
            "bpe.model"
        }.All(name => File.Exists(Path.Combine(modelDirectory, name)));
    }

    public static Task<string> InstallModelAsync(string resourceDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        EnsureModelAsync(resourceDirectory, progress, cancellationToken);

    private static async Task<string> EnsureModelAsync(string resourceDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var root = Path.Combine(resourceDirectory, "Models", "Speech", "wake-word");
        var modelDirectory = Path.Combine(root, ModelArchiveName);
        if (IsModelInstalled(resourceDirectory)) return modelDirectory;

        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, ModelArchiveName + ".tar.bz2.part");
        var extraction = Path.Combine(root, ModelArchiveName + ".extract");
        try
        {
            progress?.Report("Downloading the 19 MB wake-word model from the official Sherpa-ONNX release…");
            using (var response = await Http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            if (!File.Exists(tar)) throw new FileNotFoundException("Windows tar.exe is required to install the local wake-word model.", tar);
            var list = await RunTarAsync(tar, ["-tf", archive], cancellationToken).ConfigureAwait(false);
            if (list.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(IsUnsafeArchiveEntry))
                throw new InvalidDataException("The downloaded wake-word archive contains an unsafe path.");
            if (Directory.Exists(extraction)) Directory.Delete(extraction, true);
            Directory.CreateDirectory(extraction);
            await RunTarAsync(tar, ["-xf", archive, "-C", extraction], cancellationToken).ConfigureAwait(false);
            var extractedModel = Directory.GetDirectories(extraction, ModelArchiveName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("The wake-word model archive has an unexpected layout.");
            foreach (var name in new[] { "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx", "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx", "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx", "tokens.txt", "bpe.model" })
                if (!File.Exists(Path.Combine(extractedModel, name))) throw new InvalidDataException($"The downloaded wake-word model is missing {name}.");
            if (Directory.Exists(modelDirectory)) Directory.Delete(modelDirectory, true);
            Directory.Move(extractedModel, modelDirectory);
            progress?.Report("Wake-word model is ready; starting real-time detection.");
            return modelDirectory;
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (Directory.Exists(extraction)) Directory.Delete(extraction, true); } catch { }
        }
    }

    private static async Task<string> RunTarAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Windows could not start tar.exe.");
        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await output.ConfigureAwait(false);
        var stderr = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException("Could not unpack the wake-word model: " + stderr);
        return stdout;
    }

    private static bool IsUnsafeArchiveEntry(string entry)
    {
        var normalized = entry.Replace('\\', '/');
        return Path.IsPathRooted(entry) || normalized.StartsWith('/') || normalized.Split('/').Any(part => part == "..");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _devices.Dispose();
    }
}
