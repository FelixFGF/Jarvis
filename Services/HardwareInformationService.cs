using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;

namespace Jarvis.Services;

public sealed class HardwareInformationService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatus { public uint Length = (uint)Marshal.SizeOf<MemoryStatus>(); public uint Load; public ulong TotalPhysical; public ulong AvailablePhysical; public ulong TotalPageFile; public ulong AvailablePageFile; public ulong TotalVirtual; public ulong AvailableVirtual; public ulong AvailableExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

    public object Inspect(IReadOnlyCollection<string> requested)
    {
        var categories = requested.Select(x => x.ToLowerInvariant()).ToHashSet();
        bool Wants(string value) => categories.Count == 0 || categories.Contains("all") || categories.Contains(value);
        object? cpu = null, memory = null, gpu = null, windows = null, storage = null, monitors = null;
        if (Wants("cpu")) cpu = new { model = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Unknown")?.ToString()?.Trim(), logical_processors = Environment.ProcessorCount };
        if (Wants("memory")) { var status = new MemoryStatus(); memory = GlobalMemoryStatusEx(status) ? new { total_bytes = status.TotalPhysical, available_bytes = status.AvailablePhysical } : new { error = "Windows did not provide memory status." }; }
        if (Wants("gpu")) gpu = ReadDisplayAdapters();
        if (Wants("windows")) windows = new { description = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(), framework = RuntimeInformation.FrameworkDescription };
        if (Wants("storage")) storage = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { name = d.Name, format = d.DriveFormat, total_bytes = d.TotalSize, free_bytes = d.AvailableFreeSpace }).ToArray();
        if (Wants("monitors")) monitors = System.Windows.Forms.Screen.AllScreens.Select(s => new { id = s.DeviceName, s.Primary, x = s.Bounds.X, y = s.Bounds.Y, width = s.Bounds.Width, height = s.Bounds.Height }).ToArray();
        return new { detected = true, cpu, memory, gpu, windows, storage, monitors, note = "Values are reported by local Windows APIs and the registry; unavailable fields are omitted or marked as errors." };
    }

    private static string[] ReadDisplayAdapters()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            foreach (var id in root?.GetSubKeyNames() ?? [])
            foreach (var index in new[] { "0000", "0001" })
            {
                using var key = root?.OpenSubKey(id + "\\" + index);
                if (key?.GetValue("Device Description") is string description && !string.IsNullOrWhiteSpace(description)) values.Add(description);
            }
        }
        catch (Exception) { }
        return values.ToArray();
    }
}
