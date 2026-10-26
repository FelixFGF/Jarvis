using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

public sealed record MinecraftInstance(string Root, string Launcher, string? Name, DateTimeOffset EvidenceTime, string? LatestLog, string? CrashReport);
public sealed record MinecraftFinding(string Severity, string Code, string Summary, string Evidence, IReadOnlyList<string> SuggestedActions);
public sealed record MinecraftDiagnosis(MinecraftInstance Instance, IReadOnlyList<MinecraftFinding> Findings, IReadOnlyList<string> ModFiles);

public static class MinecraftDiagnosisService
{
    public static IReadOnlyList<MinecraftInstance> Discover(IEnumerable<string> candidateRoots)
    {
        var results = new List<MinecraftInstance>();
        foreach (var candidate in candidateRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string root;
            try { root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate)); } catch { continue; }
            if (!Directory.Exists(root)) continue;
            foreach (var instanceRoot in EnumerateInstanceRoots(root))
            {
                var latest = Path.Combine(instanceRoot, "logs", "latest.log");
                var crash = Directory.Exists(Path.Combine(instanceRoot, "crash-reports"))
                    ? Directory.EnumerateFiles(Path.Combine(instanceRoot, "crash-reports"), "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
                if (!File.Exists(latest) && crash is null && !Directory.Exists(Path.Combine(instanceRoot, "mods"))) continue;
                var evidence = new[] { File.Exists(latest) ? File.GetLastWriteTimeUtc(latest) : DateTime.MinValue, crash is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(crash) }.Max();
                results.Add(new(instanceRoot, IdentifyLauncher(root, instanceRoot), Path.GetFileName(instanceRoot), new DateTimeOffset(DateTime.SpecifyKind(evidence, DateTimeKind.Utc)), File.Exists(latest) ? latest : null, crash));
            }
        }
        return results.GroupBy(item => item.Root, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.EvidenceTime).First()).OrderByDescending(item => item.EvidenceTime).ToArray();
    }

    public static MinecraftDiagnosis Diagnose(MinecraftInstance instance)
    {
        var findings = new List<MinecraftFinding>();
        var logPath = instance.CrashReport ?? instance.LatestLog;
        var text = logPath is null ? "" : ReadTail(logPath, 1_500_000);
        AddKnownFindings(text, findings);
        var mods = Directory.Exists(Path.Combine(instance.Root, "mods"))
            ? Directory.EnumerateFiles(Path.Combine(instance.Root, "mods"), "*.jar").Select(Path.GetFileName).Where(name => name is not null).Cast<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var duplicates = mods.GroupBy(NormalizedModKey, StringComparer.OrdinalIgnoreCase).Where(group => group.Key.Length > 2 && group.Count() > 1).ToArray();
        foreach (var duplicate in duplicates) findings.Add(new("error", "duplicate-mod", $"Multiple versions of {duplicate.Key} appear installed.", string.Join(", ", duplicate), ["Back up the duplicate files and keep only the version matching this Minecraft instance."]));
        if (findings.Count == 0) findings.Add(new("info", "unclassified", "No known deterministic crash signature was found.", logPath is null ? "No latest.log or crash report was found." : $"Inspected {Path.GetFileName(logPath)}.", ["Inspect the final exception and caused-by chain manually.", "Confirm Minecraft, loader and Java versions before changing mods."]));
        return new(instance, findings, mods);
    }

    public static string CreateBackup(string instanceRoot, IEnumerable<string> files, string backupRoot)
    {
        instanceRoot = Path.GetFullPath(instanceRoot); backupRoot = Path.GetFullPath(backupRoot);
        Directory.CreateDirectory(backupRoot);
        var archive = Path.Combine(backupRoot, $"minecraft-repair-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var file in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!file.StartsWith(instanceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) throw new UnauthorizedAccessException("Backup files must exist inside the selected Minecraft instance.");
            zip.CreateEntryFromFile(file, Path.GetRelativePath(instanceRoot, file), CompressionLevel.Optimal);
        }
        return archive;
    }

    private static IEnumerable<string> EnumerateInstanceRoots(string root)
    {
        yield return root;
        foreach (var container in new[] { "instances", "profiles" })
        {
            var path = Path.Combine(root, container); if (!Directory.Exists(path)) continue;
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                yield return child;
                var minecraft = Path.Combine(child, ".minecraft"); if (Directory.Exists(minecraft)) yield return minecraft;
            }
        }
    }
    private static string IdentifyLauncher(string scannedRoot, string instance) =>
        scannedRoot.Contains("PrismLauncher", StringComparison.OrdinalIgnoreCase) ? "Prism Launcher" :
        scannedRoot.Contains("Modrinth", StringComparison.OrdinalIgnoreCase) ? "Modrinth App" :
        File.Exists(Path.Combine(instance, "instance.cfg")) ? "Prism-compatible" : "Minecraft/unknown launcher";
    private static string ReadTail(string path, int maxChars) { using var reader = new StreamReader(path); var value = reader.ReadToEnd(); return value.Length <= maxChars ? value : value[^maxChars..]; }
    private static string NormalizedModKey(string name) => Regex.Replace(Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), @"[-_. ]?(?:v)?\d+(?:[._-]\d+)*(?:[-+].*)?$", "");
    private static void AddKnownFindings(string text, List<MinecraftFinding> findings)
    {
        if (Regex.IsMatch(text, "UnsupportedClassVersionError|class file version", RegexOptions.IgnoreCase)) findings.Add(new("error", "java-version", "The configured Java runtime is incompatible with a loaded class.", ExtractEvidence(text, "UnsupportedClassVersionError", "class file version"), ["Select the Java version required by this Minecraft and loader version."]));
        if (Regex.IsMatch(text, "requires.+(?:fabric|forge|neoforge|minecraft)|Could not find required mod", RegexOptions.IgnoreCase)) findings.Add(new("error", "missing-dependency", "A mod dependency or required version is missing.", ExtractEvidence(text, "Could not find required mod", "requires"), ["Resolve the named dependency for the exact Minecraft version and loader from an official project source."]));
        if (Regex.IsMatch(text, "DuplicateModsFoundException|duplicate mod", RegexOptions.IgnoreCase)) findings.Add(new("error", "duplicate-mod", "The loader reported duplicate mods.", ExtractEvidence(text, "DuplicateModsFoundException", "duplicate mod"), ["Back up and disable only the duplicate version identified by the loader."]));
        if (Regex.IsMatch(text, "OutOfMemoryError", RegexOptions.IgnoreCase)) findings.Add(new("error", "memory", "Java ran out of memory.", ExtractEvidence(text, "OutOfMemoryError"), ["Review the instance memory allocation and unusually large mod/resource usage."]));
        if (Regex.IsMatch(text, "MixinApplyError|MixinTransformerError", RegexOptions.IgnoreCase)) findings.Add(new("warning", "mixin-conflict", "A mixin failed while modifying game code.", ExtractEvidence(text, "MixinApplyError", "MixinTransformerError"), ["Use the caused-by chain to identify the owning mod before replacing or disabling anything."]));
    }
    private static string ExtractEvidence(string text, params string[] markers)
    {
        foreach (var line in text.Replace("\r", "").Split('\n').Reverse()) if (markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase))) return line.Trim().Length > 500 ? line.Trim()[..500] : line.Trim();
        return "Signature detected in diagnostic log.";
    }
}
