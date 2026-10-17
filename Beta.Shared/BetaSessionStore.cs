using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Jarvis.Beta;

public sealed record BetaSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId, string? Email = null, DiscordProfile? DiscordProfile = null);

/// <summary>Stores Supabase session tokens protected by Windows DPAPI for the current user.</summary>
public sealed class BetaSessionStore(string path)
{
    private readonly string _path = path;

    public async Task<BetaSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var clear = WindowsDpapi.Unprotect(protectedBytes);
            try { return JsonSerializer.Deserialize<BetaSession>(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            await DeleteAsync(cancellationToken);
            return null;
        }
    }

    public async Task SaveAsync(BetaSession session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var protectedBytes = WindowsDpapi.Protect(clear);
            await File.WriteAllBytesAsync(_path, protectedBytes, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (FileNotFoundException) { }
        return Task.CompletedTask;
    }
}

internal static class WindowsDpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] value) => Transform(value, true);
    public static byte[] Unprotect(byte[] value) => Transform(value, false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required to protect beta sessions.");
        var input = new DataBlob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            DataBlob output;
            var success = protect
                ? CryptProtectData(ref input, "Jarvis beta session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero && output.Length > 0)
                {
                    var zeros = new byte[output.Length];
                    Marshal.Copy(zeros, 0, output.Data, zeros.Length);
                    CryptographicOperations.ZeroMemory(zeros);
                }
                _ = LocalFree(output.Data);
            }
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                var zeros = new byte[value.Length];
                Marshal.Copy(zeros, 0, input.Data, zeros.Length);
                CryptographicOperations.ZeroMemory(zeros);
                Marshal.FreeHGlobal(input.Data);
            }
        }
    }
}
