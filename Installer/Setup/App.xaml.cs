using System.Windows;
using System.IO.Compression;
using System.IO;
using MessageBox = System.Windows.MessageBox;

namespace Jarvis.Setup;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0].Equals("--verify-payload", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var payload = typeof(App).Assembly.GetManifestResourceStream("Jarvis.Setup.payload.zip") ?? throw new InvalidDataException("embedded payload missing");
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
                var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
                if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidDataException("duplicate archive entry");
                if (names.Any(name => name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part == ".."))) throw new InvalidDataException("unsafe archive path");
                foreach (var required in new[] { "Jarvis.exe", "Jarvis.dll", "Jarvis.deps.json", "Jarvis.runtimeconfig.json", "runtimes/win-x64/whisper.dll" })
                    if (!names.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("missing " + required);
                // This self-test runs before the WPF dispatcher is pumped. Do not block
                // the UI synchronization context while the async fixture HTTP requests await.
                Task.Run(Jarvis.Setup.MainWindow.VerifyBackendDetectionCasesAsync).GetAwaiter().GetResult();
                Environment.Exit(0);
            }
            catch { Environment.Exit(2); }
        }
        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Jarvis Setup konnte nicht gestartet werden", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
