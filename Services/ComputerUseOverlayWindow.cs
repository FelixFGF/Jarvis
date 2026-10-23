using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DrawingRectangle = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace Jarvis.Services;

/// <summary>
/// A transparent, click-through desktop frame and a small interactive stop banner.
/// The frame never activates or intercepts the applications underneath it.
/// </summary>
public sealed class ComputerUseOverlayWindow : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private readonly Window _frame;
    private readonly Window _banner;
    private readonly Canvas _cursorCanvas;
    private readonly Ellipse _cursorHalo;
    private readonly DispatcherTimer _cursorTimer;
    private readonly TextBlock _statusText;
    private readonly Border _pulseBorder;
    private readonly System.Windows.Controls.Button _pauseButton;
    private DrawingRectangle _targetBounds;
    private bool _disposed;

    public event Action? StopRequested;
    public event Action? PauseRequested;

    public ComputerUseOverlayWindow(string language)
    {
        _targetBounds = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
        if (_targetBounds.IsEmpty) _targetBounds = Forms.SystemInformation.VirtualScreen;
        _cursorCanvas = new Canvas { IsHitTestVisible = false };
        _cursorHalo = new Ellipse
        {
            Width = 34, Height = 34,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 185, 156, 255)),
            StrokeThickness = 2,
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(System.Windows.Media.Color.FromArgb(52, 171, 130, 255), .12),
                new GradientStop(System.Windows.Media.Color.FromArgb(15, 125, 82, 240), .58),
                new GradientStop(System.Windows.Media.Color.FromArgb(0, 100, 60, 220), 1)
            }),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(155, 112, 255), BlurRadius = 18, ShadowDepth = 0, Opacity = .75 }
        };
        _cursorCanvas.Children.Add(_cursorHalo);
        var screenBorder = new Border
        {
            Margin = new Thickness(5),
            BorderThickness = new Thickness(3),
            BorderBrush = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(System.Windows.Media.Color.FromArgb(100, 115, 74, 230), 0),
                new GradientStop(System.Windows.Media.Color.FromArgb(225, 190, 151, 255), .28),
                new GradientStop(System.Windows.Media.Color.FromArgb(145, 126, 95, 245), .62),
                new GradientStop(System.Windows.Media.Color.FromArgb(210, 167, 124, 255), 1)
            }, 0),
            CornerRadius = new CornerRadius(15),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(128, 84, 255), BlurRadius = 24, ShadowDepth = 0, Opacity = .55 }
        };
        screenBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.62, .95, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var frameGrid = new Grid { IsHitTestVisible = false };
        frameGrid.Children.Add(new Border { Height = 76, VerticalAlignment = VerticalAlignment.Top, Background = EdgeFade(90), IsHitTestVisible = false });
        frameGrid.Children.Add(new Border { Height = 76, VerticalAlignment = VerticalAlignment.Bottom, Background = EdgeFade(270), IsHitTestVisible = false });
        frameGrid.Children.Add(new Border { Width = 76, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Background = EdgeFade(0), IsHitTestVisible = false });
        frameGrid.Children.Add(new Border { Width = 76, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Background = EdgeFade(180), IsHitTestVisible = false });
        frameGrid.Children.Add(screenBorder);
        frameGrid.Children.Add(_cursorCanvas);
        _frame = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = _targetBounds.Left, Top = _targetBounds.Top,
            Width = Math.Max(1, _targetBounds.Width), Height = Math.Max(1, _targetBounds.Height),
            Content = frameGrid
        };
        _frame.SourceInitialized += (_, _) => MakeClickThrough(_frame, _targetBounds);

        _statusText = new TextBlock { Text = "Jarvis is controlling your computer", Foreground = System.Windows.Media.Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var stop = new System.Windows.Controls.Button
        {
            Content = language == "de" ? "Stopp" : "Stop",
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 51, 122)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(154, 119, 255)),
            Padding = new Thickness(12, 5, 12, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = language == "de" ? "Desktopsteuerung sofort stoppen" : "Stop desktop control immediately"
        };
        stop.Click += (_, _) => StopRequested?.Invoke();
        _pauseButton = new System.Windows.Controls.Button
        {
            Content = language == "de" ? "Pause" : "Pause",
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(47, 43, 66)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(115, 99, 157)),
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = language == "de" ? "Zwischen Werkzeugaktionen pausieren" : "Pause between tool actions"
        };
        _pauseButton.Click += (_, _) => PauseRequested?.Invoke();
        var logo = new System.Windows.Controls.Image
        {
            Width = 20, Height = 20, Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Jarvis.png")),
            Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center
        };
        _pulseBorder = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 24, 20, 38)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 132, 94, 236)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(14, 9, 10, 9),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(139, 92, 246), BlurRadius = 16, ShadowDepth = 0, Opacity = .5 }
        };
        _pulseBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.82, 1, TimeSpan.FromSeconds(1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(logo); row.Children.Add(_statusText); row.Children.Add(_pauseButton); row.Children.Add(stop);
        _pulseBorder.Child = row;
        _banner = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = _targetBounds.Left + Math.Max(12, (_targetBounds.Width - 380) / 2),
            Top = _targetBounds.Top + 14, Content = _pulseBorder
        };

        _cursorTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _cursorTimer.Tick += UpdateCursor;
    }

    public void Show(string state, DrawingRectangle targetBounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetTargetBounds(targetBounds);
        SetStatus(state);
        if (!_frame.IsVisible) _frame.Show();
        if (!_banner.IsVisible) _banner.Show();
        PositionWindows();
        _cursorTimer.Start();
    }

    public void SetTargetBounds(DrawingRectangle targetBounds)
    {
        if (_disposed || targetBounds.Width <= 0 || targetBounds.Height <= 0) return;
        _targetBounds = targetBounds;
        if (_frame.IsVisible || new System.Windows.Interop.WindowInteropHelper(_frame).Handle != IntPtr.Zero)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_frame).Handle;
            MakeClickThrough(_frame, _targetBounds);
            if (handle != IntPtr.Zero) SetWindowPos(handle, HwndTopmost, targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height, SwpNoActivate | SwpShowWindow);
        }
        if (_banner.IsVisible) PositionWindows();
    }

    public void SetStatus(string state)
    {
        _statusText.Text = state;
    }

    public void SetPaused(bool paused, string language)
    {
        _pauseButton.Content = paused ? (language == "de" ? "Fortsetzen" : "Resume") : (language == "de" ? "Pause" : "Pause");
        _pauseButton.ToolTip = paused
            ? (language == "de" ? "Computersteuerung fortsetzen" : "Resume computer control")
            : (language == "de" ? "Zwischen Werkzeugaktionen pausieren" : "Pause between tool actions");
    }

    public void MarkAction()
    {
        if (_disposed || !_frame.IsVisible) return;
        var ring = new Ellipse
        {
            Width = 14, Height = 14, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 190, 158, 255)),
            StrokeThickness = 2, Fill = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.MediumPurple, BlurRadius = 10, ShadowDepth = 0, Opacity = .6 }
        };
        _cursorCanvas.Children.Add(ring);
        var position = CurrentCursorDip();
        Canvas.SetLeft(ring, position.X - 7); Canvas.SetTop(ring, position.Y - 7);
        var grow = new DoubleAnimation(14, 52, TimeSpan.FromMilliseconds(380));
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380));
        fade.Completed += (_, _) => _cursorCanvas.Children.Remove(ring);
        ring.BeginAnimation(FrameworkElement.WidthProperty, grow); ring.BeginAnimation(FrameworkElement.HeightProperty, grow); ring.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cursorTimer.Stop();
        _cursorTimer.Tick -= UpdateCursor;
        _frame.Close();
        _banner.Close();
    }

    private void UpdateCursor(object? sender, EventArgs e)
    {
        var point = CurrentCursorDip();
        Canvas.SetLeft(_cursorHalo, point.X - _cursorHalo.Width / 2);
        Canvas.SetTop(_cursorHalo, point.Y - _cursorHalo.Height / 2);
    }

    private System.Windows.Point CurrentCursorDip()
    {
        if (!GetCursorPos(out var cursor)) return new System.Windows.Point(0, 0);
        var dpi = VisualTreeHelper.GetDpi(_frame);
        return new System.Windows.Point((cursor.X - _targetBounds.Left) / dpi.DpiScaleX, (cursor.Y - _targetBounds.Top) / dpi.DpiScaleY);
    }

    private void MakeClickThrough(Window window, DrawingRectangle bounds)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        SetWindowPos(handle, HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpNoActivate | SwpShowWindow);
    }

    private void PositionWindows()
    {
        var frame = new System.Windows.Interop.WindowInteropHelper(_frame).Handle;
        if (frame != IntPtr.Zero) SetWindowPos(frame, HwndTopmost, _targetBounds.Left, _targetBounds.Top, _targetBounds.Width, _targetBounds.Height, SwpNoActivate | SwpShowWindow);
        var banner = new System.Windows.Interop.WindowInteropHelper(_banner).Handle;
        if (banner == IntPtr.Zero) return;
        var rect = new NativeRect();
        if (!GetWindowRect(banner, out rect)) return;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var left = _targetBounds.Left + Math.Max(12, (_targetBounds.Width - width) / 2);
        SetWindowPos(banner, HwndTopmost, left, _targetBounds.Top + 14, width, height, SwpNoActivate | SwpShowWindow);
    }

    private static LinearGradientBrush EdgeFade(double angle) => new(new GradientStopCollection
    {
        new GradientStop(System.Windows.Media.Color.FromArgb(28, 151, 106, 255), 0),
        new GradientStop(System.Windows.Media.Color.FromArgb(12, 109, 76, 218), .38),
        new GradientStop(System.Windows.Media.Color.FromArgb(0, 78, 48, 180), 1)
    }, angle);

    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
}
