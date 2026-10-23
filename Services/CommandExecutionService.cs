using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Services;

/// <summary>Runs user-approved shell jobs with bounded concurrency, lifetime, and retained output.</summary>
public sealed class CommandExecutionService : IDisposable
{
    private const int MaximumOutputCharacters = 64_000;
    private const int MaximumActiveJobs = 4;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, CommandJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaximumActiveJobs, MaximumActiveJobs);
    private bool _disposed;

    public async Task<CommandJobSnapshot> StartAsync(string shell, string command, string workingDirectory,
        int timeoutSeconds, IReadOnlyCollection<string> allowedRoots, CancellationToken requestToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (command.Length is < 1 or > 30_000 || command.Any(c => c == '\0')) throw new ArgumentException("Command must contain 1–30,000 characters and no NUL characters.", nameof(command));
        if (timeoutSeconds is < 1 or > 900) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 900 seconds.");
        var cwd = ValidateWorkingDirectory(workingDirectory, allowedRoots);
        shell = shell.Trim().ToLowerInvariant();
        if (shell is not ("powershell" or "cmd")) throw new ArgumentException("Supported shells are powershell and cmd.", nameof(shell));
        await _slots.WaitAsync(requestToken);
        try
        {
            requestToken.ThrowIfCancellationRequested();
            var process = new Process { StartInfo = CreateStartInfo(shell, command, cwd), EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("The command process could not be started.");
            var id = Guid.NewGuid().ToString("N");
            var job = new CommandJob(id, shell, cwd, DateTimeOffset.UtcNow, process);
            if (!_jobs.TryAdd(id, job)) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("Could not register the command job."); }
            job.RequestCancellation = requestToken.Register(() => CancelJob(job, "cancelled"));
            job.Monitor = MonitorAsync(job, timeoutSeconds);
            return job.Snapshot();
        }
        catch { _slots.Release(); throw; }
    }

    public CommandJobSnapshot Get(string id)
    {
        Prune();
        return _jobs.TryGetValue(id, out var job) ? job.Snapshot() : throw new KeyNotFoundException("Command job was not found or has expired.");
    }

    public CommandJobSnapshot Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) throw new KeyNotFoundException("Command job was not found or has expired.");
        CancelJob(job, "cancelled");
        return job.Snapshot();
    }

    public static string ValidateWorkingDirectory(string path, IReadOnlyCollection<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Working directory must be an absolute path.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Working directory does not exist.");
        var permitted = allowedRoots.Any(root =>
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
            var allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
        if (!permitted) throw new UnauthorizedAccessException("Working directory is outside Jarvis's approved folders.");
        return full;
    }

    private static ProcessStartInfo CreateStartInfo(string shell, string command, string cwd)
    {
        var start = new ProcessStartInfo
        {
            FileName = shell == "cmd" ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (shell == "cmd")
        {
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/s"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(command);
        }
        else
        {
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("RemoteSigned"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(command);
        }
        return start;
    }

    private async Task MonitorAsync(CommandJob job, int timeoutSeconds)
    {
        try
        {
            var stdout = PumpAsync(job, job.Process.StandardOutput, false);
            var stderr = PumpAsync(job, job.Process.StandardError, true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await job.Process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (job.State == "running") { CancelJob(job, "timed_out"); }
            }
            await Task.WhenAll(stdout, stderr);
            if (job.State == "running")
            {
                job.ExitCode = job.Process.ExitCode;
                job.State = job.ExitCode == 0 ? "completed" : "failed";
            }
        }
        catch (Exception ex)
        {
            job.AppendError("\n[Jarvis process monitor] " + ex.Message);
            if (job.State == "running") job.State = "failed";
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.RequestCancellation.Dispose();
            job.Process.Dispose();
            _slots.Release();
        }
    }

    private static async Task PumpAsync(CommandJob job, StreamReader reader, bool error)
    {
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) return;
            var output = new string(buffer, 0, count);
            if (error) job.AppendError(output); else job.AppendOutput(output);
        }
    }

    private static void CancelJob(CommandJob job, string state)
    {
        lock (job.Sync)
        {
            if (job.State != "running") return;
            job.State = state;
            try { if (!job.Process.HasExited) job.Process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private void Prune()
    {
        var expired = DateTimeOffset.UtcNow - Retention;
        foreach (var pair in _jobs)
            if (pair.Value.FinishedAt is { } finished && finished < expired && _jobs.TryRemove(pair.Key, out _)) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var job in _jobs.Values) CancelJob(job, "cancelled");
    }

    private sealed class CommandJob(string id, string shell, string cwd, DateTimeOffset started, Process process)
    {
        public object Sync { get; } = new();
        public string Id { get; } = id;
        public string Shell { get; } = shell;
        public string WorkingDirectory { get; } = cwd;
        public DateTimeOffset StartedAt { get; } = started;
        public Process Process { get; } = process;
        public string State { get; set; } = "running";
        public int? ExitCode { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public CancellationTokenRegistration RequestCancellation { get; set; }
        public Task? Monitor { get; set; }
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        public void AppendOutput(string value) { lock (Sync) AppendTail(_stdout, value); }
        public void AppendError(string value) { lock (Sync) AppendTail(_stderr, value); }
        public CommandJobSnapshot Snapshot() { lock (Sync) return new(Id, Shell, State, WorkingDirectory, StartedAt, FinishedAt, ExitCode, Redact(_stdout.ToString()), Redact(_stderr.ToString())); }
        private static void AppendTail(StringBuilder builder, string value)
        {
            builder.Append(value);
            if (builder.Length > MaximumOutputCharacters) builder.Remove(0, builder.Length - MaximumOutputCharacters);
        }
        private static string Redact(string value)
        {
            value = Regex.Replace(value, @"(?i)\bBearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer [REDACTED]");
            value = Regex.Replace(value, @"(?i)\b(?:sb_secret_[A-Za-z0-9_-]+|github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9]{20,})\b", "[REDACTED SECRET]");
            value = Regex.Replace(value, @"(?im)\b(api[_ -]?key|secret|password|passwd|access[_ -]?token|refresh[_ -]?token|authorization)\b\s*[:=]\s*[^\s,;]+", "$1=[REDACTED]");
            return value;
        }
    }
}

public sealed record CommandJobSnapshot(string JobId, string Shell, string State, string WorkingDirectory,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int? ExitCode, string StandardOutput, string StandardError);
