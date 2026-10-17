using System.Text.Json;
using System.IO;
using Jarvis.Models;

namespace Jarvis.Services;

public sealed class ChatStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task<List<ChatSession>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        await using var input = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document.RootElement.Deserialize<List<ChatSession>>(Options) ?? [];
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("schemaVersion", out var version) && version.GetInt32() == 1 && document.RootElement.TryGetProperty("chats", out var chats))
            return chats.Deserialize<List<ChatSession>>(Options) ?? [];
        throw new InvalidDataException("Das lokale Chatarchiv hat ein unbekanntes Format oder eine nicht unterstützte Version.");
    }

    public async Task SaveAsync(string path, IReadOnlyCollection<ChatSession> chats, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 32_768, useAsync: true))
                await JsonSerializer.SerializeAsync(output, new ChatArchive { Chats = chats.ToList() }, Options, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); _saveLock.Release(); }
    }

    private sealed class ChatArchive
    {
        public int SchemaVersion { get; init; } = 1;
        public List<ChatSession> Chats { get; init; } = [];
    }
}
