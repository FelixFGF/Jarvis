using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

public sealed record TerminalSessionSnapshot(
    string Id, string Shell, string WorkingDirectory, string State, string StandardOutput,
    string StandardError, string? RunningCommand, int? LastExitCode, DateTimeOffset StartedAt);

/// <summary>Owns real, non-elevated PowerShell and CMD processes with redirected input and output.</summary>
public sealed class TerminalSessionService : IDisposable
{
    private const int MaximumOutputCharacters = 128_000;
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;
    public event EventHandler<string>? SessionChanged;

    public IReadOnlyList<TerminalSessionSnapshot> Snapshot() => _sessions.Values
        .OrderBy(session => session.StartedAt).Select(session => session.Snapshot()).ToArray();

    public TerminalSessionSnapshot Start(string shell, string workingDirectory, IReadOnlyCollection<string> allowedRoots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        shell = shell.Trim().ToLowerInvariant();
        if (shell is not ("powershell" or "cmd")) throw new ArgumentException("Supported shells are powershell and cmd.", nameof(shell));
        var cwd = CommandExecutionService.ValidateWorkingDirectory(workingDirectory, allowedRoots);
        var start = new ProcessStartInfo
        {
            FileName = shell == "cmd" ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (shell == "cmd") { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/q"); }
        else
        {
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("RemoteSigned");
            start.ArgumentList.Add("-Command"); start.ArgumentList.Add("-");
        }
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("The terminal process could not be started.");
        var session = new TerminalSession(Guid.NewGuid().ToString("N"), shell, cwd, process);
        if (!_sessions.TryAdd(session.Id, session)) { process.Kill(true); process.Dispose(); throw new InvalidOperationException("Could not register terminal session."); }
        session.OutputPump = PumpAsync(session, process.StandardOutput, false);
        session.ErrorPump = PumpAsync(session, process.StandardError, true);
        process.Exited += (_, _) => OnExited(session);
        SessionChanged?.Invoke(this, session.Id);
        return session.Snapshot();
    }

    public async Task<TerminalSessionSnapshot> ExecuteAsync(string id, string command, CancellationToken token)
    {
        var session = GetSession(id);
        if (string.IsNullOrWhiteSpace(command) || command.Length > 30_000 || command.Contains('\0')) throw new ArgumentException("Command must contain 1–30,000 characters and no NUL characters.", nameof(command));
        string commandId;
        lock (session.Sync)
        {
            if (session.State != "ready") throw new InvalidOperationException(session.State == "running" ? "A command is already running in this terminal." : "The terminal session is closed.");
            commandId = Guid.NewGuid().ToString("N");
            session.State = "running"; session.RunningCommand = command; session.CommandId = commandId; session.LastExitCode = null;
            session.AppendOutput($"\n{(session.Shell == "cmd" ? ">" : "PS>")} {command}\n");
        }
        token.ThrowIfCancellationRequested();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        var line = session.Shell == "cmd"
            ? $"{command}\r\nset __JARVIS_EXIT=%errorlevel%\r\nfor %I in (.) do @echo __JARVIS_CWD_{commandId}__:%~fI\r\necho __JARVIS_DONE_{commandId}__:%__JARVIS_EXIT%"
            : $"$__jc=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')); Invoke-Expression $__jc; $__jok=$?; $__jec=if($LASTEXITCODE -is [int]){{$LASTEXITCODE}}elseif($__jok){{0}}else{{1}}; $__jcwd=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path)); Write-Output \"__JARVIS_CWD_{commandId}__:$__jcwd\"; Write-Output \"__JARVIS_DONE_{commandId}__:$__jec\"";
        try { await session.Process.StandardInput.WriteLineAsync(line.AsMemory(), token); await session.Process.StandardInput.FlushAsync(token); }
        catch
        {
            lock (session.Sync) { session.State = "failed"; session.RunningCommand = null; session.CommandId = null; }
            SessionChanged?.Invoke(this, id); throw;
        }
        SessionChanged?.Invoke(this, id);
        return session.Snapshot();
    }

    public TerminalSessionSnapshot Get(string id) => GetSession(id).Snapshot();
    public TerminalSessionSnapshot Cancel(string id)
    {
        var session = GetSession(id);
        lock (session.Sync)
        {
            if (session.State == "running") session.State = "cancelled";
            session.RunningCommand = null; session.CommandId = null;
        }
        Kill(session); SessionChanged?.Invoke(this, id); return session.Snapshot();
    }
    public void Close(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return;
        lock (session.Sync) { session.State = "closed"; session.RunningCommand = null; session.CommandId = null; }
        Kill(session); SessionChanged?.Invoke(this, id);
    }
    public TerminalSessionSnapshot ClearOutput(string id)
    {
        var session = GetSession(id); lock (session.Sync) { session.StandardOutput.Clear(); session.StandardError.Clear(); }
        SessionChanged?.Invoke(this, id); return session.Snapshot();
    }

    private TerminalSession GetSession(string id) => _sessions.TryGetValue(id, out var session) ? session : throw new KeyNotFoundException("Terminal session was not found.");
    private async Task PumpAsync(TerminalSession session, StreamReader reader, bool error)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!error && TryUpdateWorkingDirectory(session, line)) continue;
                if (!error && TryFinishCommand(session, line)) continue;
                lock (session.Sync) { if (error) session.AppendError(line + Environment.NewLine); else session.AppendOutput(line + Environment.NewLine); }
                SessionChanged?.Invoke(this, session.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            lock (session.Sync) session.AppendError("[terminal stream closed] " + ex.Message + Environment.NewLine);
        }
    }
    private static bool TryUpdateWorkingDirectory(TerminalSession session, string line)
    {
        string? commandId;
        lock (session.Sync) commandId = session.CommandId;
        if (commandId is null) return false;
        var marker = $"__JARVIS_CWD_{commandId}__:";
        var trimmed = line.Trim();
        var markerIndex = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return false;
        var value = trimmed[(markerIndex + marker.Length)..];
        try
        {
            var cwd = session.Shell == "powershell"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(value))
                : value;
            if (Directory.Exists(cwd)) lock (session.Sync) session.WorkingDirectory = Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException) { }
        return true;
    }
    private bool TryFinishCommand(TerminalSession session, string line)
    {
        string? commandId;
        lock (session.Sync) commandId = session.CommandId;
        if (commandId is null) return false;
        var match = Regex.Match(line.Trim(), $"__JARVIS_DONE_{Regex.Escape(commandId)}__:(-?\\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var exitCode)) return false;
        lock (session.Sync)
        {
            session.LastExitCode = exitCode; session.State = "ready"; session.RunningCommand = null; session.CommandId = null;
            session.AppendOutput($"[exit {exitCode}]\n");
        }
        SessionChanged?.Invoke(this, session.Id); return true;
    }
    private void OnExited(TerminalSession session)
    {
        lock (session.Sync)
        {
            if (session.State is not ("cancelled" or "closed")) session.State = session.State == "running" ? "failed" : "closed";
            session.RunningCommand = null; session.CommandId = null;
        }
        SessionChanged?.Invoke(this, session.Id);
    }
    private static void Kill(TerminalSession session)
    {
        try { session.Process.StandardInput.Close(); } catch { }
        try { if (!session.Process.HasExited) session.Process.Kill(true); } catch { }
        try { session.Process.Dispose(); } catch { }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var id in _sessions.Keys.ToArray()) Close(id);
    }

    private sealed class TerminalSession(string id, string shell, string cwd, Process process)
    {
        public object Sync { get; } = new(); public string Id { get; } = id; public string Shell { get; } = shell;
        public string WorkingDirectory { get; set; } = cwd; public Process Process { get; } = process; public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public string State { get; set; } = "ready"; public string? RunningCommand { get; set; } public string? CommandId { get; set; }
        public int? LastExitCode { get; set; } public StringBuilder StandardOutput { get; } = new(); public StringBuilder StandardError { get; } = new();
        public Task? OutputPump { get; set; } public Task? ErrorPump { get; set; }
        public void AppendOutput(string value) => AppendTail(StandardOutput, value); public void AppendError(string value) => AppendTail(StandardError, value);
        public TerminalSessionSnapshot Snapshot() { lock (Sync) return new(Id, Shell, WorkingDirectory, State, StandardOutput.ToString(), StandardError.ToString(), RunningCommand, LastExitCode, StartedAt); }
        private static void AppendTail(StringBuilder builder, string value) { builder.Append(value); if (builder.Length > MaximumOutputCharacters) builder.Remove(0, builder.Length - MaximumOutputCharacters); }
    }
}
