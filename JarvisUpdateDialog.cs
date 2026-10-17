using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Update.Core;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Jarvis;

public sealed class JarvisUpdateDialog : Window
{
    private readonly bool _german;
    private readonly Button _install;
    private readonly Button _view;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    public event EventHandler? InstallRequested;
    public event EventHandler? ViewRelease;

    public JarvisUpdateDialog(string currentVersion, GitHubRelease release, bool verified, string language)
    {
        _german = language == "de";
        Title = "Jarvis Update"; Width = 590; Height = 560; MinWidth = 500; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var later = ButtonText(_german ? "Später" : "Later", "#24252F"); later.Click += (_, _) => Close(); actions.Children.Add(later);
        _view = ButtonText(_german ? "Release ansehen" : "View release", "#24252F"); _view.Margin = new Thickness(8, 0, 0, 0); _view.Click += (_, _) => ViewRelease?.Invoke(this, EventArgs.Empty); actions.Children.Add(_view);
        _install = ButtonText(_german ? "Jetzt aktualisieren" : "Update now", "#7765E8"); _install.Margin = new Thickness(8, 0, 0, 0); _install.IsEnabled = verified; _install.Click += (_, _) => InstallRequested?.Invoke(this, EventArgs.Empty); actions.Children.Add(_install);
        var content = new StackPanel(); root.Children.Add(content);
        content.Children.Add(new TextBlock { Text = _german ? "Jarvis-Update verfügbar" : "Jarvis update available", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,16) });
        content.Children.Add(new TextBlock { Text = (_german ? "Installierte Version: " : "Installed version: ") + currentVersion, Foreground = Brush("#B5B5C4"), Margin = new Thickness(0,0,0,5) });
        content.Children.Add(new TextBlock { Text = (_german ? "Verfügbare Version: " : "Available version: ") + release.Version, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush("#A99CFF"), Margin = new Thickness(0,0,0,14) });
        var card = new Border { Background = Brush("#1A1B23"), BorderBrush = Brush("#343540"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14) };
        var notes = new TextBox { Text = string.IsNullOrWhiteSpace(release.Notes) ? (_german ? "Keine Versionshinweise veröffentlicht." : "No release notes were published.") : release.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Foreground = Brush("#D6D5DE"), BorderThickness = new Thickness(0), MinHeight = 120, MaxHeight = 190 };
        card.Child = notes; content.Children.Add(card);
        _status = new TextBlock { Text = verified ? (_german ? "Das Update-Paket ist signiert und verifiziert." : "The update package is signed and verified.") : (_german ? "Das Update-Paket konnte nicht kryptografisch verifiziert werden. Die Installation ist gesperrt." : "The update package could not be cryptographically verified. Installation is disabled."), TextWrapping = TextWrapping.Wrap, Foreground = verified ? Brush("#B5B5C4") : Brush("#FF9D9D"), Margin = new Thickness(0,16,0,8) }; content.Children.Add(_status);
        _progress = new ProgressBar { Height = 8, Visibility = Visibility.Collapsed, Foreground = Brush("#8974FF"), Background = Brush("#292A34") }; content.Children.Add(_progress);
    }

    public void SetProgress(long received, long? total)
    {
        _progress.Visibility = Visibility.Visible;
        if (total is > 0) { _progress.IsIndeterminate = false; _progress.Maximum = total.Value; _progress.Value = received; _status.Text = (_german ? "Download: " : "Downloaded: ") + $"{received / 1048576d:F1} / {total.Value / 1048576d:F1} MB"; }
        else { _progress.IsIndeterminate = true; _status.Text = _german ? "Update wird heruntergeladen …" : "Downloading update…"; }
    }
    public void SetBusy(bool busy, string message) { _install.IsEnabled = !busy; _view.IsEnabled = !busy; _status.Text = message; _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = busy; }
    private static Button ButtonText(string text, string background) => new() { Content = text, Padding = new Thickness(14,9,14,9), Background = Brush(background), Foreground = Brushes.White, BorderBrush = Brush("#464752"), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, MinWidth = 82 };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}

