using Jarvis.Beta;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Jarvis.BetaManager;

public partial class MainWindow : Window
{
    private SupabaseOptions _options = new("", "");
    private SupabaseClient? _client;
    private BetaSession? _session;
    private BetaSessionStore? _sessionStore;
    private readonly string _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisBetaManager", "backend.json");
    private readonly string _sessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisBetaManager", "admin-session.dat");
    private readonly List<GeneratedInviteRow> _generatedCodes = [];
    private CancellationTokenSource? _avatarLoadCts;
    private static readonly HttpClient AvatarHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.OrdinalIgnoreCase);
        metadata.TryGetValue("BetaSupabaseUrl", out var url); metadata.TryGetValue("BetaSupabasePublishableKey", out var key);
        _options = SupabaseOptions.Load(_configPath, url, key);
        UrlBox.Text = SettingsUrlBox.Text = _options.Url; KeyBox.Text = SettingsKeyBox.Text = _options.PublishableKey;
        _client = new SupabaseClient(_options); _sessionStore = new BetaSessionStore(_sessionPath);
        if (!_options.IsConfigured) { ConnectionStatus.Text = "Backend not configured"; SetLoginMessage("Enter the Supabase project URL and publishable key to configure this manager."); return; }
        try
        {
            ConnectionStatus.Text = await _client.CheckConnectivityAsync() ? "Backend reachable" : "Backend unavailable";
            var saved = await _sessionStore.LoadAsync();
            if (saved is not null)
            {
                _session = saved;
                if (saved.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    var refreshed = await _client.RefreshAsync(saved.RefreshToken);
                    _session = refreshed.Session;
                }
                if (_session is not null) await OpenAdminWorkspaceAsync();
            }
        }
        catch (Exception ex) { ConnectionStatus.Text = "Backend unavailable"; SetLoginMessage(FriendlyError(ex)); }
    }

    private async void SaveBackend_Click(object sender, RoutedEventArgs e) => await SaveBackendAsync(UrlBox.Text, KeyBox.Text);
    private async void SettingsSaveBackend_Click(object sender, RoutedEventArgs e) => await SaveBackendAsync(SettingsUrlBox.Text, SettingsKeyBox.Text);
    private async Task SaveBackendAsync(string url, string key)
    {
        var candidate = new SupabaseOptions(url.Trim().TrimEnd('/'), key.Trim());
        if (!candidate.IsConfigured) { SetLoginMessage("Use an HTTPS Supabase project URL (HTTP is allowed only for localhost) and a publishable/anon key."); return; }
        try
        {
            if (_session is not null && (!string.Equals(_options.Url, candidate.Url, StringComparison.OrdinalIgnoreCase) || !string.Equals(_options.PublishableKey, candidate.PublishableKey, StringComparison.Ordinal)))
            {
                if (_client is not null) { try { await _client.SignOutAsync(_session); } catch { } }
                _session = null;
                if (_sessionStore is not null) await _sessionStore.DeleteAsync();
                ManagerTabs.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible;
            }
            candidate.Save(_configPath); _options = candidate; _client?.Dispose(); _client = new SupabaseClient(candidate);
            UrlBox.Text = SettingsUrlBox.Text = candidate.Url; KeyBox.Text = SettingsKeyBox.Text = candidate.PublishableKey;
            var reachable = await _client.CheckConnectivityAsync(); ConnectionStatus.Text = reachable ? "Backend reachable" : "Backend unavailable";
            SetLoginMessage(reachable ? "Backend settings saved and the Supabase Auth endpoint is reachable." : "Settings saved, but Supabase Auth did not respond successfully.");
            SettingsMessage.Text = reachable ? "Backend settings saved. No server secret is stored here." : "Settings saved, but the backend health check failed.";
        }
        catch (Exception ex) { SetLoginMessage(FriendlyError(ex)); SettingsMessage.Text = FriendlyError(ex); }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || !_client.IsConfigured) { SetLoginMessage("Configure the backend first."); return; }
        SignInButton.IsEnabled = false; SetLoginMessage("Signing in…");
        try
        {
            var response = await _client.SignInAsync(EmailBox.Text.Trim(), PasswordBox.Password);
            if (response.Session is null) { SetLoginMessage(response.Error ?? "Sign-in did not return a session."); return; }
            _session = response.Session;
            await OpenAdminWorkspaceAsync();
        }
        catch (Exception ex) { SetLoginMessage(FriendlyError(ex)); }
        finally { SignInButton.IsEnabled = true; PasswordBox.Clear(); }
    }

    private async Task OpenAdminWorkspaceAsync()
    {
        if (_client is null || _session is null) return;
        try
        {
            await AdminCallAsync<AdminLicenseList>("admin-list-licenses", new { search = "" });
            await _sessionStore!.SaveAsync(_session);
            LoginPanel.Visibility = Visibility.Collapsed; ManagerTabs.Visibility = Visibility.Visible;
            ConnectionStatus.Text = "Connected · " + (_session.Email ?? "administrator");
            FooterStatus.Text = "Authenticated as an authorized administrator. All records below are live backend data.";
            await RefreshDashboardAsync();
            await RefreshLicensesAsync();
            await RefreshInvitesAsync();
        }
        catch (Exception ex)
        {
            _session = null;
            if (_sessionStore is not null) await _sessionStore.DeleteAsync();
            LoginPanel.Visibility = Visibility.Visible; ManagerTabs.Visibility = Visibility.Collapsed;
            SetLoginMessage(FriendlyError(ex));
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (_client is null || _session is null) return;
        try
        {
            var result = await AdminCallAsync<AdminOverview>("admin-overview", new { });
            ActiveCount.Text = result.ActiveLicenses.ToString(); PendingCount.Text = result.PendingInvites.ToString(); InactiveCount.Text = result.InactiveLicenses.ToString();
            DashboardAuditGrid.ItemsSource = result.RecentActivity;
            FooterStatus.Text = "Dashboard refreshed from Supabase.";
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); }
    }

    private async Task RefreshLicensesAsync(string? search = null)
    {
        if (_client is null || _session is null) return;
        try
        {
            var result = await AdminCallAsync<AdminLicenseList>("admin-list-licenses", new { search = search ?? LicenseSearchBox.Text.Trim() });
            var status = (LicenseStatusFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "all";
            var filtered = status == "all" ? result.Licenses : result.Licenses.Where(license => license.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToArray();
            LicensesGrid.ItemsSource = filtered;
            FooterStatus.Text = $"Loaded {filtered.Count} of {result.Licenses.Count} license record(s) from Supabase.";
            await RefreshDashboardAsync();
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); }
    }

    private async void CreateInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _session is null) return;
        var discordId = DiscordUserIdBox.Text.Trim();
        var discordName = DiscordDisplayNameBox.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(discordId, "^[0-9]{17,20}$")) { FooterStatus.Text = "Enter a Discord User ID containing 17–20 digits."; DiscordUserIdBox.Focus(); return; }
        if (discordName.Length is < 1 or > 100) { FooterStatus.Text = "Enter a Discord display name (1–100 characters)."; DiscordDisplayNameBox.Focus(); return; }
        var expiryChoice = (InviteExpiryCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "none";
        DateTimeOffset? expiry = expiryChoice switch { "24h" => DateTimeOffset.UtcNow.AddHours(24), "7d" => DateTimeOffset.UtcNow.AddDays(7), "30d" => DateTimeOffset.UtcNow.AddDays(30), _ => null };
        if (MessageBox.Show(this, $"Generate one single-use invitation for {discordName} ({discordId})? The identity is admin-entered and is not verified by Discord. The code will be shown once and will not be sent automatically.", "Confirm invitation generation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CreateInviteButton.IsEnabled = false;
        FooterStatus.Text = "Creating invitation securely on the server…";
        try
        {
            var response = await AdminCallAsync<CreatedInviteResponse>("admin-create-invite", new { discord_user_id = discordId, discord_display_name = discordName, expires_at = expiry?.ToString("O"), note = string.IsNullOrWhiteSpace(InviteNoteBox.Text) ? null : InviteNoteBox.Text.Trim() });
            var invitation = response.Invitation;
            _generatedCodes.Insert(0, new GeneratedInviteRow(invitation.Code, invitation.InviteId, invitation.ExpiresAt));
            GeneratedCodesList.ItemsSource = null; GeneratedCodesList.ItemsSource = _generatedCodes;
            FooterStatus.Text = $"Invitation created for {discordName}. Code is single-use and was returned by Supabase; copy and deliver manually.";
            await RefreshDashboardAsync();
            await RefreshInvitesAsync();
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); }
        finally { CreateInviteButton.IsEnabled = true; }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (GeneratedCodesList.SelectedItem is not GeneratedInviteRow invite) { FooterStatus.Text = "Select an invitation code first."; return; }
        try { Clipboard.SetText(invite.Code); FooterStatus.Text = "Invitation code copied. Deliver it manually through the approved ticket."; }
        catch (Exception ex) { FooterStatus.Text = "Could not copy the code: " + ex.Message; }
    }

    private async void RefreshLicenses_Click(object sender, RoutedEventArgs e) => await RefreshLicensesAsync();

    private async void RefreshInvites_Click(object sender, RoutedEventArgs e) => await RefreshInvitesAsync();
    private async void InviteFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ManagerTabs?.Visibility == Visibility.Visible && _session is not null) await RefreshInvitesAsync();
    }
    private async Task RefreshInvitesAsync()
    {
        if (_client is null || _session is null) return;
        InvitesGrid.IsEnabled = false;
        FooterStatus.Text = "Loading invitations from Supabase…";
        try
        {
            var status = (InviteStatusFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "all";
            var list = await AdminCallAsync<AdminInviteList>("admin-list-invites", new { status, search = InviteSearchBox.Text.Trim() });
            InvitesGrid.SelectedItem = null;
            InvitesGrid.ItemsSource = list.Invitations;
            FooterStatus.Text = $"Loaded {list.Invitations.Count} invitation record(s) from Supabase.";
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); }
        finally { InvitesGrid.IsEnabled = true; }
    }

    private void InviteSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (InvitesGrid.SelectedItem is not AdminInvite invite)
        {
            RevokeInviteButton.IsEnabled = CopyInviteCodeButton.IsEnabled = DeleteInviteButton.IsEnabled = RevokeAssociatedLicenseButton.IsEnabled = false;
            return;
        }
        var rawCodeAvailable = _generatedCodes.Any(x => x.InviteId == invite.InviteId);
        CopyInviteCodeButton.IsEnabled = rawCodeAvailable;
        RevokeInviteButton.IsEnabled = invite.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);
        var deletableStatus = invite.Status is "pending" or "expired" or "revoked";
        var unused = invite.RedeemedAt is null && invite.RedeemedBy is null && invite.LicenseId is null && deletableStatus;
        DeleteInviteButton.IsEnabled = unused;
        RevokeAssociatedLicenseButton.IsEnabled = invite.LicenseId is not null && invite.LicenseStatus is "active";
        InviteDetailsText.Text = $"Discord display name: {invite.DiscordDisplayName ?? "Not provided"}  ·  ID: {invite.DiscordUserId ?? "Not provided"}\nStatus: {invite.Status}  ·  Identity: {(invite.DiscordIdentityStatus == "verified" ? "Verified" : "Admin-entered / unverified")}\nCreated: {invite.CreatedAt:yyyy-MM-dd HH:mm 'UTC'}  ·  Expires: {invite.ExpiresAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "Never"}  ·  Redeemed: {invite.RedeemedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "Not redeemed"}\nLicense: {invite.LicenseStatus ?? "None"}  ·  License ID: {invite.LicenseId ?? "None"}\nInternal note: {invite.Note ?? "None"}\nRaw code: {(rawCodeAvailable ? "available in this manager session only" : "not recoverable after creation; only its secure hash is stored")}";
    }

    private void ViewInviteDetails_Click(object sender, RoutedEventArgs e)
    {
        if (InvitesGrid.SelectedItem is not AdminInvite) { FooterStatus.Text = "Select an invitation first."; return; }
        MessageBox.Show(this, InviteDetailsText.Text, "Invitation details", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyInviteCode_Click(object sender, RoutedEventArgs e)
    {
        if (InvitesGrid.SelectedItem is not AdminInvite invite) return;
        var raw = _generatedCodes.FirstOrDefault(x => x.InviteId == invite.InviteId);
        if (raw is null) { FooterStatus.Text = "The raw invitation code is not available in this manager session."; return; }
        try { Clipboard.SetText(raw.Code); FooterStatus.Text = "Invitation code copied. Deliver it manually through the approved ticket."; }
        catch (Exception ex) { FooterStatus.Text = "Could not copy the code: " + ex.Message; }
    }

    private async void RevokeInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _session is null || InvitesGrid.SelectedItem is not AdminInvite invite || !invite.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)) return;
        var identity = $"{invite.DiscordDisplayName ?? "Unknown user"} ({invite.DiscordUserId ?? "unknown Discord ID"})";
        if (MessageBox.Show(this, $"Revoke the unused invitation assigned to {identity}? The code will immediately stop working. This action is recorded in the audit log.", "Confirm invitation revocation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RevokeInviteButton.IsEnabled = false; FooterStatus.Text = "Revoking invitation on the server…";
        try
        {
            var result = await AdminCallAsync<ApiResult>("admin-revoke-invite", new { invite_id = invite.InviteId });
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? result.Message ?? "Invitation could not be revoked.");
            await RefreshInvitesAsync(); await RefreshDashboardAsync(); await RefreshAuditAsync();
            FooterStatus.Text = $"Invitation for {identity} was revoked. The audit log was refreshed.";
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); RevokeInviteButton.IsEnabled = true; }
    }

    private async void DeleteInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _session is null || InvitesGrid.SelectedItem is not AdminInvite invite || invite.LicenseId is not null || invite.RedeemedAt is not null || invite.RedeemedBy is not null) return;
        var identity = $"{invite.DiscordDisplayName ?? "Unknown user"} ({invite.DiscordUserId ?? "unknown Discord ID"})";
        if (MessageBox.Show(this, $"Permanently delete this unused invitation for {identity}? An audit event will be retained. This cannot be undone.", "Confirm invitation deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        DeleteInviteButton.IsEnabled = false; FooterStatus.Text = "Deleting unused invitation on the server…";
        try
        {
            var result = await AdminCallAsync<ApiResult>("admin-delete-invite", new { invite_id = invite.InviteId });
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? result.Message ?? "Invitation could not be deleted.");
            _generatedCodes.RemoveAll(x => x.InviteId == invite.InviteId);
            await RefreshInvitesAsync(); await RefreshDashboardAsync(); await RefreshAuditAsync();
            FooterStatus.Text = $"Unused invitation for {identity} deleted; the audit event was preserved.";
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); DeleteInviteButton.IsEnabled = true; }
    }

    private async void RevokeAssociatedLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _session is null || InvitesGrid.SelectedItem is not AdminInvite invite || invite.LicenseId is null || invite.LicenseStatus != "active") return;
        var identity = $"{invite.DiscordDisplayName ?? "Unknown user"} ({invite.DiscordUserId ?? "unknown Discord ID"})";
        if (MessageBox.Show(this, $"Revoke the active Jarvis license associated with {identity}? The invitation history will be retained.", "Confirm license revocation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RevokeAssociatedLicenseButton.IsEnabled = false; FooterStatus.Text = "Revoking associated license on the server…";
        try
        {
            var result = await AdminCallAsync<ApiResult>("admin-revoke-license", new { license_id = invite.LicenseId });
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? result.Message ?? "License could not be revoked.");
            await RefreshInvitesAsync(); await RefreshLicensesAsync(); await RefreshAuditAsync();
            FooterStatus.Text = $"The license for {identity} was revoked. Invitation and audit history remain available.";
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); RevokeAssociatedLicenseButton.IsEnabled = true; }
    }
    private async void LicenseSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _avatarLoadCts?.Cancel(); _avatarLoadCts?.Dispose(); _avatarLoadCts = new CancellationTokenSource();
        var cancellationToken = _avatarLoadCts.Token;
        if (LicensesGrid.SelectedItem is not AdminLicense license) { LicenseDetailsText.Text = "Select a member to view account, invitation, identity status, and internal note details."; LicenseAvatar.Source = null; LicenseAvatarFallback.Text = "J"; LicenseAvatarFallback.Visibility = Visibility.Visible; return; }
        LicenseAvatar.Source = null; LicenseAvatarFallback.Text = (license.DiscordDisplayName ?? "J").Trim() is { Length: > 0 } initial ? initial[..1].ToUpperInvariant() : "J"; LicenseAvatarFallback.Visibility = Visibility.Visible;
        LicenseDetailsText.Text = $"Jarvis/Supabase account: {license.Email ?? license.UserId} ({license.UserId})\nDiscord global display name: {license.DiscordGlobalName ?? license.DiscordDisplayName ?? "Not provided"}\nDiscord display name: {license.DiscordDisplayName ?? "Not provided"}\nDiscord username: {license.DiscordUsername ?? "Not available"}\nDiscord User ID: {license.DiscordUserId ?? "Not provided"}\nDiscord identity: {(license.DiscordIdentityStatus == "verified" ? "Verified" : "Admin-entered / unverified")}\nInvitation created: {license.InviteCreatedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "Unknown"} · redeemed: {license.InviteRedeemedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "Unknown"}\nInternal note: {license.Note ?? "None"}";
        await LoadLicenseAvatarAsync(license, cancellationToken);
    }

    private async Task LoadLicenseAvatarAsync(AdminLicense license, CancellationToken cancellationToken)
    {
        if (license.DiscordUserId is null || license.DiscordAvatarUrl is null || !System.Text.RegularExpressions.Regex.IsMatch(license.DiscordUserId, "^[0-9]{17,20}$")) return;
        try
        {
            var uri = new Uri(license.DiscordAvatarUrl, UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.discordapp.com" ||
                !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, $"^/avatars/{System.Text.RegularExpressions.Regex.Escape(license.DiscordUserId)}/[A-Za-z0-9_]{{2,128}}\\.(png|jpe?g|webp|gif)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
            using var response = await AvatarHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 5_242_880 || mediaType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) return;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream(); var buffer = new byte[16 * 1024]; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) { if (bytes.Length + read > 5_242_880) return; await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
            bytes.Position = 0; var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 96; bitmap.DecodePixelHeight = 96; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
            if (cancellationToken.IsCancellationRequested || LicensesGrid.SelectedItem is not AdminLicense selected || selected.LicenseId != license.LicenseId) return;
            LicenseAvatar.Source = bitmap; LicenseAvatarFallback.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or NotSupportedException or FormatException or InvalidOperationException)
        { FooterStatus.Text = "Discord avatar could not be loaded; the fallback image remains available."; }
    }
    private async void LicenseFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ManagerTabs?.Visibility == Visibility.Visible && _session is not null) await RefreshLicensesAsync();
    }
    private async void Revoke_Click(object sender, RoutedEventArgs e) => await ChangeLicenseAsync("revoke");
    private async void Reinstate_Click(object sender, RoutedEventArgs e) => await ChangeLicenseAsync("reinstate");
    private async Task ChangeLicenseAsync(string action)
    {
        if (_client is null || _session is null || LicensesGrid.SelectedItem is not AdminLicense license) { FooterStatus.Text = "Select a license first."; return; }
        var verb = action == "revoke" ? "revoke" : "reinstate";
        if (MessageBox.Show(this, $"Are you sure you want to {verb} the license for {license.Email ?? license.UserId}?", "Confirm license change", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var result = await AdminCallAsync<ApiResult>(action == "revoke" ? "admin-revoke-license" : "admin-reinstate-license", new { license_id = license.LicenseId });
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? result.Message ?? "License operation failed.");
            FooterStatus.Text = $"License {verb}d successfully.";
            await RefreshLicensesAsync();
        }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); }
    }

    private async void RefreshAudit_Click(object sender, RoutedEventArgs e) => await RefreshAuditAsync();
    private async Task RefreshAuditAsync()
    {
        if (_client is null || _session is null) return;
        try { var result = await AdminCallAsync<AdminAuditList>("admin-audit-log", new { limit = 250 }); AuditGrid.ItemsSource = result.Events; FooterStatus.Text = $"Loaded {result.Events.Count} audit event(s)."; }
        catch (Exception ex) { FooterStatus.Text = FriendlyError(ex); }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try { if (_client is null) throw new InvalidOperationException("Backend is not configured."); var ok = await _client.CheckConnectivityAsync(); SettingsMessage.Text = ok ? "Supabase Auth endpoint is reachable." : "Supabase Auth endpoint did not return success."; ConnectionStatus.Text = ok ? "Backend reachable" : "Backend unavailable"; }
        catch (Exception ex) { SettingsMessage.Text = FriendlyError(ex); ConnectionStatus.Text = "Backend unavailable"; }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null && _client is not null) { try { await _client.SignOutAsync(_session); } catch { /* Clear the local credential even if the service is offline. */ } }
        _session = null; if (_sessionStore is not null) await _sessionStore.DeleteAsync();
        ManagerTabs.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible; ConnectionStatus.Text = "Signed out"; FooterStatus.Text = "Administrator session ended.";
    }

    private async Task<T> AdminCallAsync<T>(string function, object body)
    {
        if (_client is null || _session is null) throw new InvalidOperationException("Sign in to the beta backend first.");
        if (_session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var refreshed = await _client.RefreshAsync(_session.RefreshToken);
            if (refreshed.Session is null) { _session = null; if (_sessionStore is not null) await _sessionStore.DeleteAsync(); throw new InvalidOperationException("Administrator session expired. Sign in again."); }
            _session = refreshed.Session;
            if (_sessionStore is not null) await _sessionStore.SaveAsync(_session);
        }
        return await _client.InvokeAdminAsync<T>(function, _session, body);
    }

    private void SetLoginMessage(string text) => LoginMessage.Text = text;
    private static string FriendlyError(Exception ex) => ex switch
    {
        SupabaseRequestException request when request.StatusCode == 403 => "This account is not authorized as a beta administrator.",
        SupabaseRequestException request when request.StatusCode == 401 => "Sign-in expired. Sign in again.",
        SupabaseRequestException request when request.StatusCode == 429 => "Too many requests. Wait briefly and try again.",
        SupabaseRequestException request => request.Message,
        HttpRequestException => "Could not reach Supabase. Check the project URL and network connection.",
        TaskCanceledException => "The backend request timed out.",
        _ => ex.Message
    };

    protected override void OnClosed(EventArgs e) { _avatarLoadCts?.Cancel(); _avatarLoadCts?.Dispose(); _client?.Dispose(); base.OnClosed(e); }
}

public sealed record GeneratedInviteRow(string Code, string InviteId, DateTimeOffset? ExpiresAt)
{
    public override string ToString() => ExpiresAt is null ? Code : $"{Code}  ·  expires {ExpiresAt:yyyy-MM-dd HH:mm} UTC";
}
public sealed record CreatedInviteResponse([property: JsonPropertyName("invitation")] InviteCreated Invitation);
public sealed record AdminOverview([property: JsonPropertyName("active_licenses")] int ActiveLicenses, [property: JsonPropertyName("pending_invites")] int PendingInvites, [property: JsonPropertyName("inactive_licenses")] int InactiveLicenses, [property: JsonPropertyName("recent_activity")] IReadOnlyList<AdminAuditEvent> RecentActivity);
