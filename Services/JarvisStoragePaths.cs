using System.Security.Cryptography;
using System.IO;

namespace Jarvis.Services;

public sealed record StorageMigrationResult(int CopiedFiles, int SkippedFiles, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

public sealed class JarvisStoragePaths
{
    public const string ProductDirectoryName = "Jarvis-FGF";
    public const string RootMarkerFileName = "jarvis-storage-root.txt";

    public JarvisStoragePaths(string installRoot)
    {
        InstallRoot = Path.GetFullPath(installRoot);
        DataRoot = Path.Combine(InstallRoot, "Data");
        ConfigRoot = Path.Combine(InstallRoot, "Config");
        SvgRoot = Path.Combine(DataRoot, "SVG");
        ChatsRoot = Path.Combine(DataRoot, "Chats");
        LogsRoot = Path.Combine(DataRoot, "Logs");
        CacheRoot = Path.Combine(DataRoot, "Cache");
        BackupsRoot = Path.Combine(DataRoot, "Backups");
        VoiceRoot = Path.Combine(DataRoot, "Voice");
        ModelsRoot = Path.Combine(DataRoot, "Models");
    }

    public string InstallRoot { get; }
    public string DataRoot { get; }
    public string ConfigRoot { get; }
    public string SvgRoot { get; }
    public string ChatsRoot { get; }
    public string LogsRoot { get; }
    public string CacheRoot { get; }
    public string BackupsRoot { get; }
    public string VoiceRoot { get; }
    public string ModelsRoot { get; }

    public static string NormalizeSelectedInstallPath(string selectedPath)
    {
        var full = Path.GetFullPath(selectedPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(full), ProductDirectoryName, StringComparison.OrdinalIgnoreCase)) return full;
        return Path.Combine(full + Path.DirectorySeparatorChar, ProductDirectoryName);
    }

    public static JarvisStoragePaths CreateDefault()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("JARVIS_STORAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return new JarvisStoragePaths(overrideRoot);
        var applicationRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var marker = Path.Combine(applicationRoot, RootMarkerFileName);
        if (File.Exists(marker))
        {
            var markedRoot = File.ReadAllText(marker).Trim();
            if (!string.IsNullOrWhiteSpace(markedRoot) && Path.IsPathFullyQualified(markedRoot)) return new JarvisStoragePaths(markedRoot);
        }
        if (string.Equals(Path.GetFileName(applicationRoot), "App", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(applicationRoot)), ProductDirectoryName, StringComparison.OrdinalIgnoreCase))
            applicationRoot = Path.GetDirectoryName(applicationRoot)!;
        return new JarvisStoragePaths(applicationRoot);
    }

    public void EnsureUsedDirectories()
    {
        foreach (var path in new[] { DataRoot, ConfigRoot, SvgRoot, ChatsRoot, LogsRoot, CacheRoot, BackupsRoot, VoiceRoot, ModelsRoot })
            Directory.CreateDirectory(path);
    }

    public StorageMigrationResult MigrateLegacyData(string legacyRoot)
    {
        var copied = 0; var skipped = 0; var errors = new List<string>();
        if (!Directory.Exists(legacyRoot) || PathsEqual(legacyRoot, InstallRoot)) return new(copied, skipped, errors);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVG"] = SvgRoot, ["Models"] = ModelsRoot, ["DiscordAvatars"] = Path.Combine(CacheRoot, "DiscordAvatars"),
            ["MinecraftBackups"] = Path.Combine(BackupsRoot, "Minecraft"), ["SpeechCache"] = Path.Combine(VoiceRoot, "Cache")
        };
        foreach (var mapping in mappings)
            CopyTree(Path.Combine(legacyRoot, mapping.Key), mapping.Value, ref copied, ref skipped, errors);
        foreach (var file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var targetRoot = name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ? ConfigRoot
                : name.Contains("chat", StringComparison.OrdinalIgnoreCase) ? ChatsRoot
                : name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? LogsRoot : DataRoot;
            CopyVerified(file, Path.Combine(targetRoot, name), ref copied, ref skipped, errors);
        }
        return new(copied, skipped, errors);
    }

    private static void CopyTree(string source, string destination, ref int copied, ref int skipped, List<string> errors)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            CopyVerified(file, Path.Combine(destination, Path.GetRelativePath(source, file)), ref copied, ref skipped, errors);
    }

    private static void CopyVerified(string source, string destination, ref int copied, ref int skipped, List<string> errors)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) { skipped++; return; }
            var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(source, temporary, false);
            if (!SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(temporary))))
                throw new IOException("Migration verification failed.");
            File.Move(temporary, destination); copied++;
        }
        catch (Exception ex) { errors.Add($"{source}: {ex.Message}"); }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
