using Jarvis.Services;
using NAudio.Wave;
using System.IO;

internal static class SpeechAudioChecks
{
    public static void Run(string directory)
    {
        var quiet = BitConverter.GetBytes(0.001f);
        var extensible = new WaveFormatExtensible(48000, 32, 2);
        if (Math.Abs(LocalSpeechService.MeasureLevel(quiet, quiet.Length, extensible) - 0.001f) > 0.00001f)
            throw new Exception("Extensible float audio misread as integer PCM");
        Console.WriteLine("PASS: WASAPI extensible float noise level remains 0.001 instead of false speech-level PCM.");
        foreach (var rate in new[] { 16000, 44100, 48000 })
        foreach (var channels in new[] { 1, 2 })
        {
            var path = Path.Combine(directory, $"synthetic-{rate}-{channels}.wav");
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)))
                for (var n = 0; n < rate; n++)
                    for (var c = 0; c < channels; c++) writer.WriteSample((float)(0.4 * Math.Sin(2 * Math.PI * 440 * n / rate)));
            using var source = new WaveFileReader(path);
            using var converted = LocalSpeechService.ConvertToMono16k(source);
            using var result = new WaveFileReader(converted);
            if (result.WaveFormat.SampleRate != 16000 || result.WaveFormat.Channels != 1 || result.WaveFormat.BitsPerSample != 16 || Math.Abs(result.TotalTime.TotalSeconds - 1) > 0.02)
                throw new Exception("Audio format/duration mismatch");
            var samples = result.ToSampleProvider(); var buffer = new float[16000]; var count = samples.Read(buffer,0,buffer.Length);
            var peak = buffer.Take(count).Max(Math.Abs);
            if (peak < 0.35 || peak > 0.45) throw new Exception("Conversion changed gain or clipped");
        }
        if (WhisperSpeechRecognitionProvider.WhisperLanguage("de-DE") != "de" || WhisperSpeechRecognitionProvider.WhisperLanguage("en-US") != "en" || WhisperSpeechRecognitionProvider.WhisperLanguage("automatic") != null)
            throw new Exception("Whisper language mapping");
        foreach (var text in new[] { "Hinausgeht", "Chavez erklärt etwas", "Jarvisprojekt öffnen", "Was geht?", "Öffne Firefox, bitte." })
            if (LocalSpeechService.StripLeadingWakePhrase(text) != text) throw new Exception("Transcript was corrupted");
        if (LocalSpeechService.StripLeadingWakePhrase("Hey Jarvis, was geht?") != "was geht?") throw new Exception("Wake prefix");
        var multi = Path.Combine(directory, "unsupported.wav");
        using (var writer = new WaveFileWriter(multi, new WaveFormat(16000,16,4))) writer.Write(new byte[128],0,128);
        using var input = new WaveFileReader(multi);
        try { using var unexpected = LocalSpeechService.ConvertToMono16k(input); throw new Exception("Accepted unsupported channels"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: six synthetic PCM conversions preserve duration/gain; German/English decoder language mapping; wake-prefix boundaries and unchanged colloquial text; unsupported channels rejected.");
    }
}
