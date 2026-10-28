using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.IO.Compression;

namespace Jarvis.Services;

public sealed record ModrinthVersion(string ProjectId, string VersionId, string Name, string VersionNumber, IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders, IReadOnlyList<string> Dependencies, string DownloadUrl, string FileName, string? Sha512, long Size);

public sealed class ModrinthApiService(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private readonly bool _owns = client is null;
    public async Task<IReadOnlyList<ModrinthVersion>> FindCompatibleAsync(string projectIdOrSlug, string minecraftVersion, string loader, CancellationToken token)
    {
        var project = projectIdOrSlug?.Trim() ?? ""; var gameVersion = minecraftVersion?.Trim() ?? "";
        if (!Regex.IsMatch(project, "^[A-Za-z0-9_-]{2,64}$")) throw new ArgumentException("Invalid Modrinth project ID or slug.");
        if (!Regex.IsMatch(gameVersion, "^[0-9A-Za-z._+-]{1,32}$")) throw new ArgumentException("Invalid Minecraft version.");
        loader = (loader ?? "").Trim().ToLowerInvariant(); if (loader is not ("fabric" or "forge" or "neoforge" or "quilt")) throw new ArgumentException("Unsupported loader.");
        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(project)}/version?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}&featured=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("Jarvis/1.1.2 (local Windows assistant)");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Modrinth returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return ParseVersions(document.RootElement, gameVersion, loader);
    }
    public static IReadOnlyList<ModrinthVersion> ParseVersions(JsonElement root, string minecraftVersion, string loader)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Modrinth version response must be an array.");
        var results = new List<ModrinthVersion>();
        foreach (var item in root.EnumerateArray())
        {
            var gameVersions = Strings(item, "game_versions"); var loaders = Strings(item, "loaders");
            if (!gameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase) || !loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) continue;
            if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            var file = files.EnumerateArray().FirstOrDefault(candidate => candidate.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
            if (file.ValueKind == JsonValueKind.Undefined) file = files.EnumerateArray().FirstOrDefault();
            var url = Text(file, "url"); var filename = Text(file, "filename");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || filename is null || !filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            var dependencies = item.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array
                ? deps.EnumerateArray().Select(dep => Text(dep, "project_id") ?? Text(dep, "version_id")).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray() : [];
            var sha = file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha512", out var shaNode) ? shaNode.GetString() : null;
            results.Add(new(Text(item, "project_id") ?? "", Text(item, "id") ?? "", Text(item, "name") ?? "", Text(item, "version_number") ?? "", gameVersions, loaders, dependencies, uri.AbsoluteUri, filename, sha, file.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
        }
        return results;
    }
    public async Task<string> DownloadVerifiedAsync(ModrinthVersion version, string destinationDirectory, CancellationToken token)
    {
        if (!Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.EndsWith("modrinth.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected download is not an HTTPS Modrinth CDN URL.");
        if (string.IsNullOrWhiteSpace(version.Sha512) || !Regex.IsMatch(version.Sha512, "^[0-9a-fA-F]{128}$"))
            throw new InvalidDataException("Modrinth did not provide a valid SHA-512 hash for this file.");
        var safeName = Path.GetFileName(version.FileName);
        if (!safeName.Equals(version.FileName, StringComparison.Ordinal) || !safeName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Modrinth filename is unsafe.");
        destinationDirectory = Path.GetFullPath(destinationDirectory); Directory.CreateDirectory(destinationDirectory);
        var finalPath = Path.Combine(destinationDirectory, safeName);
        var tempPath = finalPath + ".jarvis-download-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("Jarvis/1.1.2 (local Windows assistant)");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Modrinth CDN returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            if (version.Size > 0 && response.Content.Headers.ContentLength is long length && length != version.Size)
                throw new InvalidDataException("The download size differs from Modrinth metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            if (version.Size > 0 && new FileInfo(tempPath).Length != version.Size) throw new InvalidDataException("The downloaded file is incomplete.");
            await using (var stream = File.OpenRead(tempPath))
            {
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(stream, token));
                if (!hash.Equals(version.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded mod failed SHA-512 verification.");
            }
            using (var archive = ZipFile.OpenRead(tempPath))
                if (archive.GetEntry("META-INF/MANIFEST.MF") is null && archive.GetEntry("fabric.mod.json") is null && archive.GetEntry("META-INF/mods.toml") is null && archive.GetEntry("META-INF/neoforge.mods.toml") is null)
                    throw new InvalidDataException("The verified download is not recognizable as a mod JAR.");
            File.Move(tempPath, finalPath, false);
            return finalPath;
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }
    private static string[] Strings(JsonElement item, string name) => item.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Array ? node.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];
    private static string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    public void Dispose() { if (_owns) _http.Dispose(); }
}
