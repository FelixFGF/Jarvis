using Jarvis.Services;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Jarvis;

public sealed class ImageLightboxWindow : Window
{
    private readonly IReadOnlyList<WebImageResult> _images;
    private readonly string _language;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5, .5) };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _attribution = new() { Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 192)), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 192)) };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private CancellationTokenSource? _load;
    private int _index;
    private Point? _dragStart;
    private Vector _translateStart;

    public ImageLightboxWindow(IReadOnlyList<WebImageResult> images, int initialIndex, string language)
    {
        _images = images; _index = ImageGalleryInteraction.ClampIndex(initialIndex, images.Count); _language = language;
        Title = language == "de" ? "Bildanzeige" : "Image viewer"; Width = 1050; Height = 760; MinWidth = 680; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(14, 14, 20));
        var transforms = new TransformGroup(); transforms.Children.Add(_scale); transforms.Children.Add(_translate); _image.RenderTransform = transforms;
        var imageHost = new Border { Background = new SolidColorBrush(Color.FromRgb(9, 9, 13)), CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _image };
        imageHost.MouseWheel += ImageHost_MouseWheel; imageHost.MouseLeftButtonDown += ImageHost_MouseLeftButtonDown; imageHost.MouseLeftButtonUp += ImageHost_MouseLeftButtonUp; imageHost.MouseMove += ImageHost_MouseMove;
        var previous = Button(language == "de" ? "‹ Zurück" : "‹ Previous", (_, _) => Navigate(-1));
        var next = Button(language == "de" ? "Weiter ›" : "Next ›", (_, _) => Navigate(1));
        var fit = Button(language == "de" ? "Einpassen" : "Fit", (_, _) => Fit());
        var download = Button(language == "de" ? "Herunterladen …" : "Download…", async (_, _) => await DownloadAsync());
        var source = Button(language == "de" ? "Quelle öffnen" : "Open source", (_, _) => OpenSource());
        var close = Button(language == "de" ? "Schließen" : "Close", (_, _) => Close());
        var toolbar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var button in new[] { previous, next, fit, download, source, close }) toolbar.Children.Add(button);
        var details = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; details.Children.Add(_title); details.Children.Add(_attribution); details.Children.Add(_status); details.Children.Add(toolbar);
        var root = new Grid { Margin = new Thickness(16) }; root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(imageHost); Grid.SetRow(details, 1); root.Children.Add(details); Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key == Key.Left) Navigate(-1); else if (e.Key == Key.Right) Navigate(1); };
        Closed += (_, _) => { _load?.Cancel(); _load?.Dispose(); };
        Loaded += async (_, _) => await LoadCurrentAsync();
    }

    private static Button Button(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), MinHeight = 32 };
        button.Click += action; return button;
    }
    private async void Navigate(int delta) { _index = ImageGalleryInteraction.WrapIndex(_index + delta, _images.Count); await LoadCurrentAsync(); }
    private void Fit() { _scale.ScaleX = _scale.ScaleY = 1; _translate.X = _translate.Y = 0; }
    private async Task LoadCurrentAsync()
    {
        if (_images.Count == 0) return; _load?.Cancel(); _load?.Dispose(); _load = new CancellationTokenSource(); Fit(); _image.Source = null;
        var item = _images[_index]; _title.Text = $"{_index + 1}/{_images.Count} · {item.Title}";
        _attribution.Text = string.Join(" · ", new[] { item.Provider, item.Author, item.License }.Where(value => !string.IsNullOrWhiteSpace(value)));
        _status.Text = _language == "de" ? "Bild wird geladen …" : "Loading image…";
        try
        {
            using var downloader = new ImageDownloadService(); var downloaded = await downloader.FetchAsync(ActualUrl(item), _load.Token);
            using var stream = new MemoryStream(downloaded.Bytes); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _image.Source = bitmap; _status.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = (_language == "de" ? "Bild konnte nicht geladen werden: " : "Could not load image: ") + ex.Message; }
    }
    private async Task DownloadAsync()
    {
        var item = _images[_index]; _status.Text = _language == "de" ? "Download wird vorbereitet …" : "Preparing download…";
        try
        {
            using var downloader = new ImageDownloadService(); var downloaded = await downloader.FetchAsync(ActualUrl(item), CancellationToken.None);
            var dialog = new SaveFileDialog { FileName = ImageGalleryInteraction.SafeFileName(item.Title) + downloaded.Extension, Filter = $"Image (*{downloaded.Extension})|*{downloaded.Extension}|All files (*.*)|*.*", AddExtension = true, DefaultExt = downloaded.Extension };
            if (dialog.ShowDialog(this) != true) { _status.Text = _language == "de" ? "Download abgebrochen." : "Download cancelled."; return; }
            await File.WriteAllBytesAsync(dialog.FileName, downloaded.Bytes); _status.Text = _language == "de" ? "Bild gespeichert." : "Image saved.";
        }
        catch (Exception ex) { _status.Text = (_language == "de" ? "Download fehlgeschlagen: " : "Download failed: ") + ex.Message; }
    }
    private void OpenSource() { var url = _images[_index].SourceUrl; if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
    private void ImageHost_MouseWheel(object sender, MouseWheelEventArgs e) { var next = ImageGalleryInteraction.ClampZoom(_scale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15)); _scale.ScaleX = _scale.ScaleY = next; if (next == 1) { _translate.X = _translate.Y = 0; } }
    private void ImageHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (_scale.ScaleX <= 1) return; _dragStart = e.GetPosition(this); _translateStart = new Vector(_translate.X, _translate.Y); ((UIElement)sender).CaptureMouse(); }
    private void ImageHost_MouseMove(object sender, MouseEventArgs e) { if (_dragStart is null || e.LeftButton != MouseButtonState.Pressed) return; var point = e.GetPosition(this); _translate.X = _translateStart.X + point.X - _dragStart.Value.X; _translate.Y = _translateStart.Y + point.Y - _dragStart.Value.Y; }
    private void ImageHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { _dragStart = null; ((UIElement)sender).ReleaseMouseCapture(); }
    private static string ActualUrl(WebImageResult image) => string.IsNullOrWhiteSpace(image.FullImageUrl) ? image.ThumbnailUrl : image.FullImageUrl;
}
