using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Services;

public sealed record RecentActivityEntry(DateTimeOffset Timestamp, string ProcessName, string WindowTitle, bool IsForeground);

public sealed class RecentActivityService : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private readonly ConcurrentQueue<RecentActivityEntry> _entries = new();
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    public RecentActivityService() => _callback = OnWinEvent;
    public bool IsRunning => _hook != IntPtr.Zero;
    public void Start()
    {
        if (IsRunning) return;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WineventOutofcontext | WineventSkipownprocess);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Windows foreground event monitoring could not be started.");
        Capture(GetForegroundWindow());
    }
    public void Stop()
    {
        var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
        if (hook != IntPtr.Zero) _ = UnhookWinEvent(hook);
    }
    public IReadOnlyList<RecentActivityEntry> Snapshot() => _entries.Reverse().Take(100).ToArray();
    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime) => Capture(hwnd);
    private void Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return;
        var title = new StringBuilder(512); _ = GetWindowText(hwnd, title, title.Capacity);
        if (title.Length == 0) return;
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        string processName;
        try { processName = Process.GetProcessById((int)processId).ProcessName; }
        catch { return; }
        var entry = new RecentActivityEntry(DateTimeOffset.Now, processName, title.ToString(), true);
        var last = _entries.LastOrDefault();
        if (last is not null && last.ProcessName.Equals(entry.ProcessName, StringComparison.OrdinalIgnoreCase) && last.WindowTitle == entry.WindowTitle) return;
        _entries.Enqueue(entry);
        while (_entries.Count > 100) _entries.TryDequeue(out _);
    }
    public void Dispose() => Stop();
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHookAssembly, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
