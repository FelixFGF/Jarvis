using Jarvis.Update.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System;
using System.IO;
using System.Windows.Controls;
using System.Windows;

namespace Jarvis.Updater;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var language = "en";
        for (var i = 0; i + 1 < e.Args.Length; i++) if (e.Args[i] == "--language" && e.Args[i + 1] == "de") language = "de";
        var german = language == "de";
        var window = new UpdateProgressWindow(german); MainWindow = window; window.Show();
        var progress = new Progress<string>(window.SetStatus);
        try { await ApplyAsync(e.Args, progress, german); window.Close(); Shutdown(0); }
        catch (Exception ex) { window.Close(); MessageBox.Show((german ? "Jarvis konnte das Update nicht abschließen. Die vorherigen Dateien wurden, soweit möglich, wiederhergestellt." : "Jarvis could not complete the update. Your previous files have been restored where possible.") + "\n\n" + ex.Message, "Jarvis Update", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }

    private static async Task ApplyAsync(string[] args, IProgress<string> progress, bool german)
    {
        var values = Parse(args);
        var pid = int.Parse(Required(values, "pid"));
        var install = Path.GetFullPath(Required(values, "install"));
        var package = Path.GetFullPath(Required(values, "package"));
        var manifestPath = Path.GetFullPath(Required(values, "manifest"));
        var signaturePath = Path.GetFullPath(Required(values, "signature"));
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var signature = await File.ReadAllTextAsync(signaturePath);
        if (!UpdateSecurity.VerifyManifest(manifestBytes, signature)) throw new InvalidDataException("The update signature is not trusted.");
        var manifest = UpdateSecurity.ParseManifest(manifestBytes);
        ValidateExistingInstallation(install);
        var info = new FileInfo(package);
        if (info.Length != manifest.PackageSize) throw new InvalidDataException("The update package size is invalid.");
        await using (var file = File.OpenRead(package))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update package hash does not match the signed manifest.");
        }
        progress.Report(german ? "Signiertes Update-Paket wird geprüft …" : "Verifying the signed update package…");
        var extractionVersion = manifest.Version;
        if (!Jarvis.Update.Core.SemanticVersion.TryParse(extractionVersion, out var parsedVersion) || parsedVersion is null) throw new InvalidDataException("The signed update version is invalid.");
        progress.Report(german ? "Warte, bis Jarvis geschlossen ist …" : "Waiting for Jarvis to close…");
        try { await Process.GetProcessById(pid).WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (ArgumentException) { }

        if (!CanWriteDirectory(install)) throw new UnauthorizedAccessException("Windows denied write access to the installation folder. Restart Jarvis with Run as administrator to approve elevation, or use the official setup to choose a user-writable folder.");

        var stage = Path.Combine(Path.GetTempPath(), "JarvisUpdate", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(stage, "backup"); var extracted = Path.Combine(stage, "payload");
        Directory.CreateDirectory(backup); Directory.CreateDirectory(extracted);
        try
        {
            progress.Report(german ? "Update-Dateien werden geprüft und entpackt …" : "Validating and extracting update files…");
            using (var archive = ZipFile.OpenRead(package))
            {
                if (archive.Entries.Count is 0 or > 20000) throw new InvalidDataException("The update archive has an invalid number of entries.");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName;
                    var directory = name.EndsWith('/'); var relative = directory ? name[..^1] : name;
                    if (!UpdateSecurity.IsSafeArchivePath(relative) || !paths.Add(relative)) throw new InvalidDataException("The update archive contains an unsafe or duplicate path.");
                    var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (mode == 0xA000) throw new InvalidDataException("Symbolic links are not allowed in update packages.");
                    var destination = Path.GetFullPath(Path.Combine(extracted, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(extracted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update archive path escapes its staging directory.");
                    if (directory) Directory.CreateDirectory(destination);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); await using var input = entry.Open(); await using var output = File.Create(destination); await input.CopyToAsync(output); }
                }
            }
            if (!File.Exists(Path.Combine(extracted, "Jarvis.exe"))) throw new InvalidDataException("The update archive does not contain Jarvis.exe at its root.");
            var appAssemblyPath = Path.Combine(extracted, "Jarvis.dll");
            if (!File.Exists(appAssemblyPath)) throw new InvalidDataException("The update archive is missing the Jarvis application assembly.");
            var assemblyVersion = System.Reflection.AssemblyName.GetAssemblyName(appAssemblyPath).Version;
            if (assemblyVersion is null || assemblyVersion.Major != parsedVersion.Major || assemblyVersion.Minor != parsedVersion.Minor || assemblyVersion.Build != parsedVersion.Patch) throw new InvalidDataException("The app version in the archive does not match its signed release manifest.");
            var files = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories);
            if (files.Length > 20000) throw new InvalidDataException("The update contains too many files.");
            progress.Report(german ? "Geprüfte Anwendungsdateien werden installiert …" : "Installing verified application files…");
            var changed = UpdateFileTransaction.Apply(extracted, install, backup);
            try
            {
                var app = Path.Combine(install, "Jarvis.exe");
                if (!File.Exists(app)) throw new IOException("Updated Jarvis.exe is missing after installation.");
                progress.Report(german ? "Jarvis wird neu gestartet …" : "Restarting Jarvis…");
                Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = install });
            }
            catch
            {
                UpdateFileTransaction.Restore(changed, install, backup);
                try { Process.Start(new ProcessStartInfo(Path.Combine(install, "Jarvis.exe")) { UseShellExecute = true, WorkingDirectory = install }); } catch { }
                throw;
            }
        }
        finally { try { Directory.Delete(stage, true); } catch { } }
    }

    private static bool CanWriteDirectory(string path)
    {
        try { var probe = Path.Combine(path, ".jarvis-update-write-test-" + Guid.NewGuid().ToString("N")); using (File.Create(probe)) { } File.Delete(probe); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static void ValidateExistingInstallation(string path)
    {
        if (!File.Exists(Path.Combine(path, "Jarvis.exe")) || !File.Exists(Path.Combine(path, "Jarvis.dll"))) throw new InvalidDataException("The selected folder is not a Jarvis installation.");
        var marker = Path.Combine(path, "install-manifest.json");
        if (!File.Exists(marker)) throw new InvalidDataException("The installation marker is missing. Use the official setup to update this installation.");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(marker));
        if (!document.RootElement.TryGetProperty("Product", out var product) || product.GetString() != "Jarvis") throw new InvalidDataException("The selected folder does not contain a valid Jarvis installation marker.");
    }

    private static Dictionary<string,string> Parse(string[] args)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2) { if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Invalid updater arguments."); result[args[i][2..]] = args[i + 1]; }
        return result;
    }
    private static string Required(Dictionary<string,string> values, string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new ArgumentException("Missing updater argument: " + key);
}

public sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock _status;
    private readonly bool _german;
    public UpdateProgressWindow(bool german)
    {
        _german = german;
        Title = "Jarvis Update"; Width = 430; Height = 190; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = true;
        var panel = new StackPanel { Margin = new Thickness(26), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = german ? "Jarvis wird aktualisiert" : "Updating Jarvis", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,18) });
        _status = new TextBlock { Text = german ? "Update wird vorbereitet …" : "Preparing update…", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180,178,194)), Margin = new Thickness(0,0,0,12) };
        panel.Children.Add(_status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 7, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(137,116,255)), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(41,42,52)) });
        Content = panel;
    }
    public void SetStatus(string value) => _status.Text = value;
}
