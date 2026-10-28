using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Jarvis.Services;

/// <summary>Bounded, content-addressed cache for locally persisted PCM speech.</summary>
public sealed class SpeechAudioCache(string directory, long maximumBytes = 96L * 1024 * 1024, int maximumFiles = 300)
{
    public string DirectoryPath { get; } = directory;

    public async Task<byte[]> GetOrCreateAsync(string identity, Func<CancellationToken, Task<byte[]>> generate, CancellationToken cancellationToken)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant() + ".pcm";
        var path = Path.Combine(DirectoryPath, name);
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached.Length is > 0 and <= 20_000_000)
            {
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
                return cached;
            }
            try { File.Delete(path); } catch { }
        }

        var generated = await generate(cancellationToken).ConfigureAwait(false);
        if (generated.Length is 0 or > 20_000_000) throw new InvalidDataException("Speech provider returned an invalid audio payload.");
        Directory.CreateDirectory(DirectoryPath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, generated, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
            Prune();
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        return generated;
    }

    public long SizeBytes()
    {
        if (!Directory.Exists(DirectoryPath)) return 0;
        try { return Directory.EnumerateFiles(DirectoryPath, "*.pcm").Sum(path => new FileInfo(path).Length); }
        catch { return 0; }
    }

    public void Clear()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }

    private void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(DirectoryPath, "*.pcm").Select(path => new FileInfo(path)).OrderByDescending(file => file.LastAccessTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            while (files.Count > maximumFiles || total > maximumBytes)
            {
                var oldest = files[^1]; files.RemoveAt(files.Count - 1); total -= oldest.Length;
                try { oldest.Delete(); } catch { }
            }
        }
        catch { }
    }
}
