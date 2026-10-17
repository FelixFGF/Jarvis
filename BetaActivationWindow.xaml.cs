using Jarvis.Beta;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Jarvis;

public partial class BetaActivationWindow : Window
{
    private readonly SupabaseClient _client;
    private readonly BetaSessionStore _store;
    private BetaSession? _pendingSession;
    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _avatarCts;
    private int _authGeneration;
    private static readonly HttpClient AvatarHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    public BetaSession? ActiveSession { get; private set; }
    public LicenseValidation? ActiveLicense { get; private set; }

    public BetaActivationWindow(SupabaseClient client, BetaSessionStore store) { InitializeComponent(); _client = client; _store = store; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConfigured) { SetStatus("Supabase is not configured. Build the Beta configuration with a project URL and publishable key.", true); return; }
        try
        {
            var saved = await _store.LoadAsync();
            if (saved is null) { SetStatus("Continue with Discord to sign in or activate your beta access."); return; }
            SignOutButton.Visibility = Visibility.Visible;
            var session = saved;
            if (saved.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                var refreshed = await _client.RefreshAsync(saved.RefreshToken);
                if (refreshed.Session is null) { await _store.DeleteAsync(); SetStatus("Your saved session expired. Continue with Discord again."); return; }
                session = refreshed.Session with { DiscordProfile = saved.DiscordProfile };
            }
            if (await ValidateAsync(session)) { await _store.SaveAsync(session); Accept(session); }
            else if (session.DiscordProfile is not null) ShowIdentity(session, session.DiscordProfile);
        }
        catch (SupabaseRequestException ex) { SetStatus(ex.Message, true); }
        catch (Exception) { SetStatus("The beta service could not be reached. Check your connection and try again.", true); }
    }

    private async void Discord_Click(object sender, RoutedEventArgs e)
    {
        await StartDiscordSignInAsync();
    }

    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        var generation = ++_authGeneration;
        _authCts?.Cancel();
        var previous = _pendingSession;
        _pendingSession = null;
        ActiveLicense = null;
        InviteBox.Clear();
        ClearIdentity();
        try
        {
            var saved = await _store.LoadAsync();
            var session = previous ?? saved;
            if (session is not null) { try { await _client.SignOutAsync(session); } catch { /* Local session is cleared even when offline. */ } }
            await _store.DeleteAsync();
        }
        catch (Exception) { SetBusy(false); SetStatus("The old local session could not be cleared. Retry before signing into another account.", true); return; }
        if (generation != _authGeneration) return;
        SetStatus("Starting a fresh Discord authorization. Your browser may reuse its current Discord login; choose another account in Discord if needed.");
        await StartDiscordSignInAsync();
    }

    private async Task StartDiscordSignInAsync()
    {
        _authCts?.Cancel(); _authCts?.Dispose(); _authCts = new CancellationTokenSource();
        var token = _authCts.Token;
        var generation = ++_authGeneration;
        SetBusy(true); SetStatus("Opening Discord authorization in your browser…");
        try
        {
            var result = await _client.SignInWithDiscordAsync(token);
            if (generation != _authGeneration || token.IsCancellationRequested) return;
            if (result.Session is null || result.Profile is null) { SetStatus(result.Error ?? "Discord sign-in could not be completed.", true); return; }
            _pendingSession = result.Session;
            ShowIdentity(result.Session, result.Profile);
            ActivateButton.IsEnabled = true;
            SetStatus("Discord identity authenticated. Enter the invitation code assigned to this account.");
        }
        catch (OperationCanceledException) { if (generation == _authGeneration) SetStatus("Discord sign-in was cancelled. You can try again.", true); }
        catch (Exception) { if (generation == _authGeneration) SetStatus("Could not complete Discord sign-in. Check the beta configuration and try again.", true); }
        finally { if (generation == _authGeneration) SetBusy(false); }
    }

    private void ShowIdentity(BetaSession session, DiscordProfile profile)
    {
        _pendingSession = session;
        IdentityPanel.Visibility = Visibility.Visible;
        DiscordName.Text = profile.GlobalName ?? profile.DiscordDisplayName ?? profile.DiscordUsername ?? "Discord user";
        DiscordId.Text = $"Discord ID: {profile.DiscordUserId}";
        ActivateButton.IsEnabled = true;
        SignOutButton.Visibility = Visibility.Visible;
        SwitchAccountButton.Visibility = Visibility.Visible;
        _ = LoadAvatarAsync(profile.DiscordAvatarUrl);
    }

    private async Task LoadAvatarAsync(string? url)
    {
        _avatarCts?.Cancel(); _avatarCts?.Dispose(); _avatarCts = new CancellationTokenSource();
        var token = _avatarCts.Token;
        DiscordAvatar.Source = null; AvatarFallback.Visibility = Visibility.Visible;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.discordapp.com" || !Regex.IsMatch(url, @"^https://cdn\.discordapp\.com/avatars/[0-9]{17,20}/[A-Za-z0-9_]{2,128}\.png\?size=256$")) return;
        try
        {
            using var response = await AvatarHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 2_097_152 || response.Content.Headers.ContentType?.MediaType != "image/png") return;
            var bytes = await response.Content.ReadAsByteArrayAsync(token); if (bytes.Length > 2_097_152 || token.IsCancellationRequested) return;
            using var stream = new System.IO.MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 96; image.DecodePixelHeight = 96; image.StreamSource = stream; image.EndInit(); image.Freeze();
            DiscordAvatar.Source = image; AvatarFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or NotSupportedException) { }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSession is null || _pendingSession.DiscordProfile?.DiscordUserId is null) { SetStatus("Continue with Discord before entering an invitation code.", true); return; }
        var code = InviteBox.Text.Trim();
        if (code.Length is < 10 or > 80) { SetStatus("Enter the single-use invitation code assigned to you.", true); InviteBox.Focus(); return; }
        SetBusy(true); SetStatus("Redeeming invitation securely…");
        try
        {
            var result = await _client.InvokeAsync<ApiResult>("redeem-invite", _pendingSession, new { code });
            if (!result.Ok)
            {
                var message = result.Message ?? result.Error ?? "This invitation could not be redeemed by the signed-in Discord account.";
                if (!string.IsNullOrWhiteSpace(result.DiagnosticId)) message += $" (Reference: {result.DiagnosticId})";
                SetStatus(message, true); return;
            }
            if (await ValidateAsync(_pendingSession)) { await _store.SaveAsync(_pendingSession); Accept(_pendingSession); }
        }
        catch (SupabaseRequestException ex) { SetStatus(ex.Message, true); }
        catch (Exception) { SetStatus("Activation could not be completed. Check your connection and try again.", true); }
        finally { SetBusy(false); }
    }

    private async Task<bool> ValidateAsync(BetaSession session)
    {
        SetStatus("Verifying your beta license with the server…");
        var license = await _client.InvokeAsync<LicenseValidation>("validate-license", session, new { });
        ActiveLicense = license;
        if (license.Valid) return true;
        if (license.Status is "revoked" or "expired") SetStatus("This beta license is no longer valid. Contact the Jarvis team if you believe this is an error.", true);
        else SetStatus(license.Message ?? "No active beta license was found. Enter the invitation code assigned to your Discord account.");
        return false;
    }

    private void Accept(BetaSession session) { ActiveSession = session; DialogResult = true; }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _authCts?.Cancel(); ++_authGeneration;
        try
        {
            var saved = _pendingSession ?? await _store.LoadAsync(); if (saved is not null) { try { await _client.SignOutAsync(saved); } catch { } }
            await _store.DeleteAsync(); _pendingSession = null; ActiveLicense = null; InviteBox.Clear(); ClearIdentity(); SignOutButton.Visibility = Visibility.Collapsed; SetBusy(false); SetStatus("Saved session removed. Continue with Discord to sign in again.");
        }
        catch (Exception) { SetStatus("Could not remove the saved session.", true); }
    }

    private void SetBusy(bool busy) { DiscordButton.IsEnabled = !busy; SwitchAccountButton.IsEnabled = !busy; ActivateButton.IsEnabled = !busy && _pendingSession is not null; }
    private void ClearIdentity() { _avatarCts?.Cancel(); DiscordAvatar.Source = null; AvatarFallback.Visibility = Visibility.Visible; IdentityPanel.Visibility = Visibility.Collapsed; DiscordName.Text = ""; DiscordId.Text = ""; ActivateButton.IsEnabled = false; }
    private void SetStatus(string text, bool error = false) { StatusText.Text = text; StatusText.Foreground = error ? System.Windows.Media.Brushes.Salmon : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#A09EAB")!; }
    protected override void OnClosed(EventArgs e) { _authCts?.Cancel(); _authCts?.Dispose(); _avatarCts?.Cancel(); _avatarCts?.Dispose(); base.OnClosed(e); }
}
