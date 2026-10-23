using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Threading;
#if JARVIS_BETA
using Jarvis.Beta;
#endif

namespace Jarvis;

public partial class App : System.Windows.Application
{
    static App()
    {
        // Set this before WPF creates any HWND so capture, window, and cursor APIs
        // agree on physical coordinates across monitors with different DPI.
        if (!System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2))
            System.Diagnostics.Trace.TraceWarning("Jarvis could not enable Per-Monitor-V2 DPI awareness before startup.");
    }

#if JARVIS_BETA
    private BetaSession? _session;
    private SupabaseOptions? _options;
    private SupabaseClient? _client;
    private MainWindow? _jarvisWindow;
    private DispatcherTimer? _licenseTimer;
    public BetaSessionStore? BetaSessionStore { get; private set; }
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if JARVIS_BETA
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.OrdinalIgnoreCase);
        metadata.TryGetValue("BetaSupabaseUrl", out var compiledUrl);
        metadata.TryGetValue("BetaSupabasePublishableKey", out var compiledKey);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");
        var environmentUrl = Environment.GetEnvironmentVariable("JARVIS_SUPABASE_URL");
        var environmentKey = Environment.GetEnvironmentVariable("JARVIS_SUPABASE_PUBLISHABLE_KEY");
        // An empty environment variable should not mask valid compile-time Beta metadata.
        _options = new SupabaseOptions(
            string.IsNullOrWhiteSpace(environmentUrl) ? compiledUrl ?? "" : environmentUrl,
            string.IsNullOrWhiteSpace(environmentKey) ? compiledKey ?? "" : environmentKey);
        _client = new SupabaseClient(_options);
        BetaSessionStore = new BetaSessionStore(Path.Combine(root, "beta-session.dat"));
        var gate = new BetaActivationWindow(_client, BetaSessionStore);
        MainWindow = gate;
        if (gate.ShowDialog() != true || gate.ActiveSession is null)
        {
            Shutdown();
            return;
        }
        _session = gate.ActiveSession;
        var main = new MainWindow();
        main.ApplyBetaProfile(gate.ActiveLicense?.DiscordProfile);
        _jarvisWindow = main;
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        _licenseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _licenseTimer.Tick += async (_, _) => await RevalidateBetaLicenseAsync();
        _licenseTimer.Start();
#else
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
#endif
    }

#if JARVIS_BETA
    public async Task SignOutBetaAsync()
    {
        var session = _session;
        _session = null;
        try
        {
            if (session is not null && _client is not null)
                await _client.SignOutAsync(session);
        }
        catch
        {
            // Clear the local protected session even when the server is unreachable.
        }
        if (BetaSessionStore is not null)
            await BetaSessionStore.DeleteAsync();
    }

    public async Task<bool> RefreshBetaProfileAsync()
    {
        if (_session is null || _client is null) return false;
        try
        {
            if (_session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                var refreshed = await _client.RefreshAsync(_session.RefreshToken);
                if (refreshed.Session is null) return false;
                _session = refreshed.Session;
                if (BetaSessionStore is not null) await BetaSessionStore.SaveAsync(_session);
            }
            var status = await _client.InvokeAsync<LicenseValidation>("validate-license", _session, new { });
            if (!status.Valid) return false;
            _jarvisWindow?.ApplyBetaProfile(status.DiscordProfile);
            return true;
        }
        catch
        {
            // The periodic authoritative validation remains fail-closed. A manual
            // profile refresh may use the already displayed cached profile offline.
            return false;
        }
    }

    private async Task RevalidateBetaLicenseAsync()
    {
        if (_session is null || _client is null) return;
        try
        {
            if (_session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                var refreshed = await _client.RefreshAsync(_session.RefreshToken);
                if (refreshed.Session is null) throw new InvalidOperationException("The beta session expired. Sign in again.");
                _session = refreshed.Session;
                if (BetaSessionStore is not null) await BetaSessionStore.SaveAsync(_session);
            }
            var status = await _client.InvokeAsync<LicenseValidation>("validate-license", _session, new { });
            if (status.Valid) { _jarvisWindow?.ApplyBetaProfile(status.DiscordProfile); return; }
            _licenseTimer?.Stop();
            System.Windows.MessageBox.Show(status.Message ?? "This Jarvis beta license is no longer active.", "Jarvis Beta", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        }
        catch (Exception)
        {
            // Fail closed on next validation. No local flag or unbounded offline grace grants access.
            _licenseTimer?.Stop();
            System.Windows.MessageBox.Show("Jarvis could not verify the beta license with the server. Reconnect to the internet and start the app again.", "Jarvis Beta", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _licenseTimer?.Stop();
        _client?.Dispose();
        base.OnExit(e);
    }
#endif
}
