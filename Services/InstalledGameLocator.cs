using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

public sealed record InstalledGameTarget(string Name, string Platform, string ExecutablePath, string LaunchTarget);

/// <summary>Looks up installed games from launcher-owned manifests instead of guessing common install paths.</summary>
public static class InstalledGameLocator
{
    private static readonly Regex VdfValue = new("\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SafeGameExecutable = new(@"\.exe$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] ExcludedExecutableWords = ["uninstall", "unins", "setup", "installer", "crashreport", "crashreporter", "redist", "easyanticheat", "benchmark", "prereq", "vc_redist"];

    public static InstalledGameTarget? Find(string requestedName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = Normalize(requestedName);
        if (requested.Length < 3) return null;
        return FindEpic(requested, cancellationToken) ?? FindSteam(requested, cancellationToken) ?? FindValorant(requested, cancellationToken);
    }

    private static InstalledGameTarget? FindSteam(string requested, CancellationToken cancellationToken)
    {
        foreach (var steamRoot in SteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            var folders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(folders))
                {
                    var vdf = File.ReadAllText(folders);
                    foreach (Match match in VdfValue.Matches(vdf))
                    {
                        if (!match.Groups["key"].Value.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                        var path = match.Groups["value"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                        if (Path.IsPathFullyQualified(path) && Directory.Exists(Path.Combine(path, "steamapps"))) libraries.Add(Path.GetFullPath(path));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (var library in libraries)
            {
                var apps = Path.Combine(library, "steamapps");
                IEnumerable<string> manifests;
                try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).Take(10000).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var manifest in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var content = File.ReadAllText(manifest);
                        var values = VdfValue.Matches(content).Cast<Match>().GroupBy(m => m.Groups["key"].Value, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
                        if (!values.TryGetValue("name", out var name) || Normalize(name) != requested || !values.TryGetValue("installdir", out var installName)) continue;
                        var installRoot = Path.GetFullPath(Path.Combine(apps, "common", installName));
                        if (!Directory.Exists(installRoot)) continue;
                        // The Steam URI starts the exact app through Steam (including
                        // DRM/EAC). A searchable game EXE is useful for diagnostics,
                        // but isn't required for a valid installed appmanifest.
                        var executable = FindExecutable(installRoot, requested, cancellationToken) ?? "";
                        var appId = values.TryGetValue("appid", out var id) ? id : Path.GetFileNameWithoutExtension(manifest).Replace("appmanifest_", "", StringComparison.OrdinalIgnoreCase);
                        if (!Regex.IsMatch(appId, @"^\d{1,12}$")) continue;
                        // Steam's own URI hands off DRM/dependencies to the launcher. The discovered EXE is retained for diagnostics/fallback.
                        return new(name, "Steam", executable, $"steam://rungameid/{appId}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
                }
            }
        }
        return null;
    }

    private static InstalledGameTarget? FindEpic(string requested, CancellationToken cancellationToken)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return null;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(manifests, "*.item", SearchOption.TopDirectoryOnly).Take(5000).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                string Read(string key) => root.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
                var displayName = Read("DisplayName"); var appName = Read("AppName");
                if (Normalize(displayName) != requested && Normalize(appName) != requested) continue;
                var installLocation = Read("InstallLocation");
                if (!Path.IsPathFullyQualified(installLocation) || !Directory.Exists(installLocation)) continue;
                var rootPath = Path.GetFullPath(installLocation);
                var launchExecutable = Read("LaunchExecutable");
                var executable = ResolveContainedExecutable(rootPath, launchExecutable) ?? FindExecutable(rootPath, requested, cancellationToken);
                if (executable is null) continue;
                // Let Epic's launcher handle anti-cheat, prerequisites and game IDs;
                // retain the verified EXE as a safe fallback if the URI cannot launch.
                var launchTarget = string.IsNullOrWhiteSpace(appName)
                    ? executable
                    : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";
                return new(string.IsNullOrWhiteSpace(displayName) ? appName : displayName, "Epic Games", executable, launchTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return null;
    }

    private static InstalledGameTarget? FindValorant(string requested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requested is not ("valorant" or "riotvalorant")) return null;
        var riotRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var keyPath in new[] { @"SOFTWARE\Riot Games, Inc\Riot Client", @"SOFTWARE\WOW6432Node\Riot Games, Inc\Riot Client" })
                {
                    using var key = baseKey.OpenSubKey(keyPath);
                    foreach (var valueName in new[] { "InstallLocation", "InstallPath", "RiotClientInstalls" })
                        if (key?.GetValue(valueName) is string path && Path.IsPathFullyQualified(path)) riotRoots.Add(Path.GetFullPath(path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        var riotManifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
        try
        {
            if (File.Exists(riotManifest))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(riotManifest));
                if (document.RootElement.TryGetProperty("associated_client", out var associated) && associated.ValueKind == JsonValueKind.String)
                {
                    var clientPath = associated.GetString()?.Replace('/', Path.DirectorySeparatorChar);
                    if (clientPath is not null && Path.IsPathFullyQualified(clientPath))
                    {
                        var clientDirectory = Path.GetDirectoryName(clientPath);
                        var riotRoot = clientDirectory is null ? null : Directory.GetParent(clientDirectory)?.FullName;
                        if (riotRoot is not null) riotRoots.Add(riotRoot);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        riotRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games"));
        riotRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games"));
        foreach (var root in riotRoots.Where(Directory.Exists))
        {
            var valorantExe = Path.Combine(root, "VALORANT", "live", "VALORANT.exe");
            if (!File.Exists(valorantExe)) continue;
            var client = Path.Combine(root, "Riot Client", "RiotClientServices.exe");
            if (!File.Exists(client)) client = valorantExe;
            return new("VALORANT", "Riot Games", valorantExe, client == valorantExe ? valorantExe : "riotclient://launch-product=valorant&launch-patchline=live");
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "steamapps"))) roots.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (var root in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam") })
            if (Directory.Exists(Path.Combine(root, "steamapps"))) roots.Add(Path.GetFullPath(root));
        // Steam's registry entry can point to a launcher on C: while games live in a
        // standalone library on another drive. Discover common library roots directly
        // as well, so those manifests are inspected even if libraryfolders.vdf is stale.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            foreach (var relative in new[]
            {
                "SteamLibrary", "Steam", Path.Combine("Games", "SteamLibrary"), Path.Combine("Games", "Steam"),
                Path.Combine("Program Files", "Steam"), Path.Combine("Program Files (x86)", "Steam")
            })
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, relative);
                if (Directory.Exists(Path.Combine(candidate, "steamapps"))) roots.Add(Path.GetFullPath(candidate));
            }
        }
        return roots;
    }

    private static string? FindExecutable(string installRoot, string requested, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        try
        {
            var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((installRoot, 0));
            while (pending.Count > 0 && candidates.Count < 12000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Dequeue();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                        if (IsLaunchExecutable(file)) candidates.Add(file);
                    if (depth >= 5) continue;
                    foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Take(400))
                        if (!ExcludedDirectory(Path.GetFileName(child))) pending.Enqueue((child, depth + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return candidates
            .OrderByDescending(path => Normalize(Path.GetFileNameWithoutExtension(path)) == requested)
            .ThenByDescending(path => Normalize(Path.GetFileNameWithoutExtension(path)).Contains(requested, StringComparison.Ordinal))
            .ThenBy(path => path.Count(character => character is '\\' or '/'))
            .ThenByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
    }

    private static string? ResolveContainedExecutable(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(candidate, root) || !File.Exists(candidate) || !IsLaunchExecutable(candidate)) return null;
        return candidate;
    }

    private static bool IsLaunchExecutable(string path) => SafeGameExecutable.IsMatch(path) && !ExcludedExecutableWords.Any(word => Path.GetFileNameWithoutExtension(path).Contains(word, StringComparison.OrdinalIgnoreCase));
    private static bool ExcludedDirectory(string name) => name.Equals(".egstore", StringComparison.OrdinalIgnoreCase) || name.Equals("redist", StringComparison.OrdinalIgnoreCase) || name.Contains("prereq", StringComparison.OrdinalIgnoreCase) || name.Contains("easyanticheat", StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
