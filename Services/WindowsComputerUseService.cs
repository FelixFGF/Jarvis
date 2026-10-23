using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Jarvis.Services;

/// <summary>Visible, foreground Windows input and screenshot helpers; never reads browser secrets or process memory.</summary>
public sealed class WindowsComputerUseService
{
    private const uint MouseEventLeftDown = 0x0002, MouseEventLeftUp = 0x0004, MouseEventWheel = 0x0800;
    private const uint KeyEventKeyUp = 0x0002, KeyEventUnicode = 0x0004;
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMoveAbsolute = 0x8000, MouseVirtualDesk = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);
    [DllImport("shcore.dll", PreserveSig = true)] private static extern int GetDpiForMonitor(IntPtr monitor, uint dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    public sealed record ScreenCapture(string DataUrl, ScreenshotCoordinateMap CoordinateMap, DateTime CapturedUtc);

    public ScreenCapture CaptureScreen(CancellationToken cancellationToken = default)
    {
        var captured = CaptureScreenDataUrl(System.Windows.Forms.SystemInformation.VirtualScreen, cancellationToken, out var map);
        return new ScreenCapture(captured, map, DateTime.UtcNow);
    }

    public ScreenCapture CaptureMonitor(string monitorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, monitorId, StringComparison.OrdinalIgnoreCase));
        if (screen is null) throw new InvalidOperationException("The requested monitor is no longer connected. Refresh the desktop overview.");
        var captured = CaptureScreenDataUrl(screen.Bounds, cancellationToken, out var map);
        return new ScreenCapture(captured, map, DateTime.UtcNow);
    }

    private string CaptureScreenDataUrl(Rectangle bounds, CancellationToken cancellationToken, out ScreenshotCoordinateMap coordinateMap)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("No interactive desktop screen is available.");
        const int maxDimension = 1600;
        var scale = Math.Min(1d, (double)maxDimension / Math.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        coordinateMap = new ScreenshotCoordinateMap(bounds, width, height);
        using var original = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(original)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        cancellationToken.ThrowIfCancellationRequested();
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(original, 0, 0, width, height);
        }
        using var stream = new MemoryStream();
        scaled.Save(stream, ImageFormat.Jpeg);
        if (stream.Length > 5 * 1024 * 1024) throw new InvalidDataException("Screen capture exceeds the 5 MB request limit.");
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }

    public (int X, int Y) Click(int x, int y, Rectangle expectedTargetBounds, int tolerancePixels, CancellationToken cancellationToken = default)
    {
        ValidateCoordinates(x, y);
        if (expectedTargetBounds.Width <= 0 || expectedTargetBounds.Height <= 0)
            throw new InvalidOperationException("The validated target bounds are empty.");
        var final = VerifiedMouseClick.Execute(new NativeMouseInput(), new Point(x, y), expectedTargetBounds, tolerancePixels, cancellationToken);
        return (final.X, final.Y);
    }

    public void TypeText(string text, CancellationToken cancellationToken = default)
    {
        if (text.Length is < 1 or > 2000 || text.Any(char.IsControl)) throw new ArgumentException("Text must contain 1–2,000 printable characters.", nameof(text));
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendKey((ushort)character, KeyEventUnicode);
            SendKey((ushort)character, KeyEventUnicode | KeyEventKeyUp);
        }
    }

    public void PressKey(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = ParseKeyChord(key);
        var pressed = new List<ushort>();
        try
        {
            foreach (var virtualKey in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SendKey(virtualKey, 0);
                pressed.Add(virtualKey);
            }
        }
        finally
        {
            // Always release modifiers already sent, even when cancellation arrives mid-chord.
            foreach (var virtualKey in pressed.AsEnumerable().Reverse()) SendKey(virtualKey, KeyEventKeyUp);
        }
    }

    public void Scroll(int x, int y, int delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCoordinates(x, y);
        if (delta is < -1200 or > 1200 || delta == 0) throw new ArgumentOutOfRangeException(nameof(delta));
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Windows did not accept the cursor position.");
        var input = new Input { Type = InputMouse, Union = new InputUnion { Mouse = new MouseInput { MouseData = unchecked((uint)delta), Flags = MouseEventWheel } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Windows blocked mouse-wheel input.");
    }

    public object ObserveMetadata()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var foreground = GetForegroundWindow();
        var title = new System.Text.StringBuilder(512);
        if (foreground != IntPtr.Zero) GetWindowText(foreground, title, title.Capacity);
        var point = GetCursor();
        var foregroundBounds = foreground != IntPtr.Zero && GetWindowRect(foreground, out var rect)
            ? new { x = rect.Left, y = rect.Top, width = Math.Max(0, rect.Right - rect.Left), height = Math.Max(0, rect.Bottom - rect.Top) }
            : null;
        var screens = System.Windows.Forms.Screen.AllScreens.Select(screen =>
        {
            var dpi = GetMonitorDpi(screen.Bounds);
            return new
            {
                device = screen.DeviceName,
                primary = screen.Primary,
                x = screen.Bounds.Left,
                y = screen.Bounds.Top,
                width = screen.Bounds.Width,
                height = screen.Bounds.Height,
                working_x = screen.WorkingArea.Left,
                working_y = screen.WorkingArea.Top,
                working_width = screen.WorkingArea.Width,
                working_height = screen.WorkingArea.Height,
                dpi_x = dpi.X,
                dpi_y = dpi.Y,
                scale_percent = dpi.X is null ? null : (int?)Math.Round(dpi.X.Value * 100d / 96d),
                orientation = screen.Bounds.Height > screen.Bounds.Width ? "Portrait" : "Landscape"
            };
        }).ToArray();
        var controls = foreground != IntPtr.Zero ? InspectAccessibleControls(foreground) : [];
        return new
        {
            virtual_desktop = new { x = bounds.Left, y = bounds.Top, width = bounds.Width, height = bounds.Height },
            monitors = screens,
            foreground_window = new { title = title.ToString(), bounds = foregroundBounds },
            accessible_controls = controls,
            cursor = new { x = point.X, y = point.Y },
            coordinate_guidance = "Computer Use x/y values refer to pixels in the most recently attached screenshot, whose top-left is the virtual desktop's top-left."
        };
    }

    public IReadOnlyList<MonitorGeometry> GetMonitorGeometries() => System.Windows.Forms.Screen.AllScreens
        .Select(screen =>
        {
            var dpi = GetMonitorDpi(screen.Bounds);
            return new MonitorGeometry(screen.DeviceName, screen.Bounds, screen.WorkingArea, screen.Primary, dpi.X, dpi.Y);
        }).ToArray();

    public object InspectForegroundControls()
    {
        var foreground = GetForegroundWindow();
        return foreground == IntPtr.Zero ? Array.Empty<object>() : InspectAccessibleControls(foreground);
    }

    /// <summary>Returns a bounded list of visible UI Automation controls without reading ValuePattern contents.</summary>
    private static object[] InspectAccessibleControls(IntPtr foreground)
    {
        try
        {
            var root = AutomationElement.FromHandle(foreground);
            if (root is null) return [];
            var walker = TreeWalker.ControlViewWalker;
            var result = new List<object>(48);
            var queue = new Queue<AutomationElement>();
            var first = walker.GetFirstChild(root);
            if (first is not null) queue.Enqueue(first);
            var visited = 0;
            while (queue.Count > 0 && visited++ < 240 && result.Count < 60)
            {
                var element = queue.Dequeue();
                try
                {
                    var current = element.Current;
                    var bounds = current.BoundingRectangle;
                    if (!current.IsOffscreen && bounds.Width > 0 && bounds.Height > 0
                        && (!string.IsNullOrWhiteSpace(current.Name) || !string.IsNullOrWhiteSpace(current.AutomationId)))
                    {
                        var patterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).ToArray();
                        result.Add(new
                        {
                            name = current.Name,
                            automation_id = current.AutomationId,
                            control_type = current.ControlType.ProgrammaticName,
                            is_enabled = current.IsEnabled,
                            is_offscreen = current.IsOffscreen,
                            has_keyboard_focus = current.HasKeyboardFocus,
                            bounds = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height },
                            supported_actions = patterns
                        });
                    }
                    var child = walker.GetFirstChild(element);
                    if (child is not null) queue.Enqueue(child);
                    for (var sibling = walker.GetNextSibling(element); sibling is not null; sibling = walker.GetNextSibling(sibling)) queue.Enqueue(sibling);
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }
            return result.ToArray();
        }
        catch (ElementNotAvailableException) { return []; }
        catch (InvalidOperationException) { return []; }
        catch (COMException) { return []; }
    }

    /// <summary>Invokes an exactly identified, currently visible and enabled control through UIA.</summary>
    public static string NormalizeAccessibleIdentifier(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsExactAccessibleIdentifierMatch(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected)
        && string.Equals(NormalizeAccessibleIdentifier(expected), NormalizeAccessibleIdentifier(actual), StringComparison.Ordinal);

    public static string InvokeAccessibleControl(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 200) throw new ArgumentException("Provide an exact UI Automation name or AutomationId from a fresh observation.", nameof(identifier));
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) throw new InvalidOperationException("There is no foreground window to inspect.");
        var root = AutomationElement.FromHandle(foreground);
        if (root is null) throw new InvalidOperationException("The foreground window does not expose accessible controls.");
        var matches = new List<AutomationElement>();
        var queue = new Queue<AutomationElement>();
        var walker = TreeWalker.ControlViewWalker;
        var child = walker.GetFirstChild(root);
        if (child is not null) queue.Enqueue(child);
        var visited = 0;
        while (queue.Count > 0 && visited++ < 1000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = queue.Dequeue();
            try
            {
                var current = element.Current;
                if (!current.IsOffscreen && current.IsEnabled
                    && (IsExactAccessibleIdentifierMatch(identifier, current.AutomationId)
                        || IsExactAccessibleIdentifierMatch(identifier, current.Name))) matches.Add(element);
                var nested = walker.GetFirstChild(element);
                if (nested is not null) queue.Enqueue(nested);
                for (var sibling = walker.GetNextSibling(element); sibling is not null; sibling = walker.GetNextSibling(sibling)) queue.Enqueue(sibling);
            }
            catch (ElementNotAvailableException) { }
        }
        if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0
            ? "No visible, enabled UI Automation control matched that exact identifier. Refresh the observation; no action was taken."
            : "That UI Automation identifier is ambiguous in the current window. Use a unique AutomationId; no action was taken.");
        cancellationToken.ThrowIfCancellationRequested();
        var selected = matches[0];
        if (!selected.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The identified control does not expose the UI Automation Invoke action; no action was taken.");
        ((InvokePattern)pattern).Invoke();
        return selected.Current.Name;
    }

    /// <summary>Returns the monitor containing the actual foreground window, falling back to the cursor monitor.</summary>
    public System.Drawing.Rectangle GetTargetMonitorBounds()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            var screen = System.Windows.Forms.Screen.FromHandle(foreground);
            if (!screen.Bounds.IsEmpty) return screen.Bounds;
        }
        if (GetCursorPos(out var cursor))
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            if (!screen.Bounds.IsEmpty) return screen.Bounds;
        }
        return System.Windows.Forms.SystemInformation.VirtualScreen;
    }

    public string GetForegroundWindowTitle()
    {
        var foreground = GetForegroundWindow();
        var title = new System.Text.StringBuilder(512);
        if (foreground != IntPtr.Zero) GetWindowText(foreground, title, title.Capacity);
        return title.ToString();
    }

    private static (uint? X, uint? Y) GetMonitorDpi(System.Drawing.Rectangle bounds)
    {
        try
        {
            var center = new PointNative { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
            var monitor = MonitorFromPoint(center, 2);
            return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var x, out var y) == 0 ? (x, y) : (null, null);
        }
        catch (DllNotFoundException) { return (null, null); }
        catch (EntryPointNotFoundException) { return (null, null); }
    }

    public static (int X, int Y) MapScreenshotCoordinates(ScreenshotCoordinateMap map, int x, int y) => map.ToDesktop(x, y);

    public static ushort[] ParseKeyChord(string key)
    {
        var normalized = (key ?? "").Trim().ToUpperInvariant();
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["ENTER"] = 0x0D, ["TAB"] = 0x09, ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B, ["SPACE"] = 0x20,
            ["BACKSPACE"] = 0x08, ["DELETE"] = 0x2E, ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
            ["HOME"] = 0x24, ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
            ["CTRL"] = 0x11, ["SHIFT"] = 0x10, ["ALT"] = 0x12
        };
        if (map.TryGetValue(normalized, out var single)) return [single];
        var parts = normalized.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || parts.Any(part => part is ("WIN" or "WINDOWS" or "CTRL+ALT+DELETE"))) throw new ArgumentException("Unsupported key. Use a supported key or shortcut such as CTRL+L, CTRL+A, CTRL+C, CTRL+V, CTRL+F, CTRL+TAB, or CTRL+SHIFT+TAB.", nameof(key));
        if (parts.SequenceEqual(new[] { "CTRL", "ALT", "DELETE" }, StringComparer.Ordinal)) throw new ArgumentException("Secure attention and system key sequences are not supported.", nameof(key));
        var result = new List<ushort>();
        foreach (var part in parts)
        {
            if (map.TryGetValue(part, out var mapped) && part is ("CTRL" or "SHIFT" or "ALT")) { result.Add(mapped); continue; }
            if (map.TryGetValue(part, out mapped) && part is not ("CTRL" or "SHIFT" or "ALT")) { result.Add(mapped); continue; }
            if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) { result.Add(char.IsLetter(part[0]) ? (ushort)(0x41 + char.ToUpperInvariant(part[0]) - 'A') : (ushort)(0x30 + part[0] - '0')); continue; }
            throw new ArgumentException("Unsupported key chord.", nameof(key));
        }
        if (result.Count(x => x is 0x11 or 0x10 or 0x12) != result.Count - 1) throw new ArgumentException("A shortcut must contain one supported modifier and one key.", nameof(key));
        return result.ToArray();
    }

    private static void ValidateCoordinates(int x, int y)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!bounds.Contains(x, y)) throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be inside the current virtual desktop.");
    }

    private static (int X, int Y) GetCursor()
    {
        if (!GetCursorPos(out var point)) throw new InvalidOperationException("Could not read the current cursor position.");
        return (point.X, point.Y);
    }

    private sealed class NativeMouseInput : IMouseInputController
    {
        public bool SetCursorPosition(Point position) => SetCursorPos(position.X, position.Y);
        public Point GetCursorPosition()
        {
            var point = GetCursor();
            return new Point(point.X, point.Y);
        }
        public void SendLeftClick()
        {
            var down = new Input { Type = InputMouse, Union = new InputUnion { Mouse = new MouseInput { Flags = MouseEventLeftDown } } };
            var up = new Input { Type = InputMouse, Union = new InputUnion { Mouse = new MouseInput { Flags = MouseEventLeftUp } } };
            var inputs = new[] { down, up };
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent == (uint)inputs.Length) return;
            // If Windows accepted the down event but blocked the up event, release it immediately.
            if (sent > 0) _ = SendInput(1, [up], Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows did not accept the complete mouse click input sequence.");
        }
    }

    private static void SendKey(ushort key, uint flags)
    {
        var unicode = (flags & KeyEventUnicode) != 0;
        var input = new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0, Flags = flags } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Windows blocked keyboard input.");
    }

    private struct PointNative { public int X; public int Y; }
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
}
