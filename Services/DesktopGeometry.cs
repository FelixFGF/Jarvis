using System.Drawing;

namespace Jarvis.Services;

public sealed record MonitorGeometry(string Id, Rectangle Bounds, Rectangle WorkArea, bool Primary, uint? DpiX, uint? DpiY)
{
    public string Orientation => Bounds.Height > Bounds.Width ? "Portrait" : "Landscape";
    public int? ScalePercent => DpiX is null ? null : (int)Math.Round(DpiX.Value * 100d / 96d);
}

public sealed record WindowGeometry(string Title, string ProcessName, Rectangle Bounds, bool Minimized, bool Maximized, string MonitorId, bool Fullscreen);

public static class DesktopGeometry
{
    public static MonitorGeometry? FindMonitorForWindow(Rectangle window, IReadOnlyList<MonitorGeometry> monitors)
    {
        if (monitors.Count == 0) return null;
        var intersecting = monitors
            .Select(monitor => (Monitor: monitor, Area: IntersectionArea(window, monitor.Bounds)))
            .OrderByDescending(item => item.Area)
            .First();
        return intersecting.Area > 0 ? intersecting.Monitor : null;
    }

    public static bool CoversMonitor(Rectangle window, Rectangle monitor, int tolerance = 2) =>
        Math.Abs(window.Left - monitor.Left) <= tolerance
        && Math.Abs(window.Top - monitor.Top) <= tolerance
        && Math.Abs(window.Right - monitor.Right) <= tolerance
        && Math.Abs(window.Bottom - monitor.Bottom) <= tolerance;

    private static long IntersectionArea(Rectangle left, Rectangle right)
    {
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        return (long)width * height;
    }
}
