using Jarvis.Services;
using System.IO;

internal static class TerminalChecks
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
        Check(WorkspaceLayoutState.Parse("Browser", true) == WorkspaceLayoutMode.Browser &&
            WorkspaceLayoutState.Parse("Browser", false) == WorkspaceLayoutMode.Hidden &&
            WorkspaceLayoutState.WorkspaceView(WorkspaceLayoutMode.Terminal) == "Terminal" &&
            WorkspaceLayoutState.IsMaximized(WorkspaceLayoutMode.Changes), "workspace visibility and exclusive modes resolve deterministically");
        Check(!TaskCompletionPolicy.ShouldShow(0, 0) && TaskCompletionPolicy.ShouldShow(1, 0) && TaskCompletionPolicy.ShouldShow(0, 1), "ordinary answers omit completion cards while real file or tool work enables them");

        var root = Path.Combine(Path.GetTempPath(), "jarvis-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(!WorkspaceLayoutState.IsAvailable("") && WorkspaceLayoutState.IsAvailable(root), "workspace and terminal actions stay unavailable until a valid project folder exists");
            using var service = new TerminalSessionService();
            var ps = service.Start("powershell", root, [root]);
            await service.ExecuteAsync(ps.Id, "Write-Output 'powershell-ok'; Get-Location", CancellationToken.None);
            var psDone = await WaitReadyAsync(service, ps.Id);
            Check(psDone.LastExitCode == 0 && psDone.StandardOutput.Contains("powershell-ok", StringComparison.Ordinal) && psDone.WorkingDirectory == Path.GetFullPath(root), "persistent PowerShell session returns output, working directory and exit code");
            var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
            await service.ExecuteAsync(ps.Id, $"Set-Location -LiteralPath '{child.Replace("'", "''")}'", CancellationToken.None);
            psDone = await WaitReadyAsync(service, ps.Id);
            Check(string.Equals(psDone.WorkingDirectory, Path.GetFullPath(child), StringComparison.OrdinalIgnoreCase), "PowerShell cd updates the visible effective working directory");
            await service.ExecuteAsync(ps.Id, "$jarvisPersistentValue = 41", CancellationToken.None);
            await WaitReadyAsync(service, ps.Id);
            await service.ExecuteAsync(ps.Id, "Write-Output ($jarvisPersistentValue + 1)", CancellationToken.None);
            psDone = await WaitReadyAsync(service, ps.Id);
            Check(psDone.StandardOutput.Contains("42", StringComparison.Ordinal), "PowerShell variables persist between commands");
            await service.ExecuteAsync(ps.Id, "[Console]::Error.WriteLine('terminal-error')", CancellationToken.None);
            psDone = await WaitReadyAsync(service, ps.Id);
            Check(psDone.StandardError.Contains("terminal-error", StringComparison.OrdinalIgnoreCase), "PowerShell stderr remains visible");
            await service.ExecuteAsync(ps.Id, "cmd /c exit 7", CancellationToken.None);
            psDone = await WaitReadyAsync(service, ps.Id);
            Check(psDone.LastExitCode == 7, "native nonzero exit codes remain visible in PowerShell sessions");

            var cmd = service.Start("cmd", root, [root]);
            await service.ExecuteAsync(cmd.Id, "echo cmd-ok", CancellationToken.None);
            var cmdDone = await WaitReadyAsync(service, cmd.Id);
            Check(cmdDone.LastExitCode == 0 && cmdDone.StandardOutput.Contains("cmd-ok", StringComparison.OrdinalIgnoreCase), "persistent CMD session returns real output and exit code");
            await service.ExecuteAsync(cmd.Id, $"cd /d \"{child}\"", CancellationToken.None);
            cmdDone = await WaitReadyAsync(service, cmd.Id);
            Check(string.Equals(cmdDone.WorkingDirectory, Path.GetFullPath(child), StringComparison.OrdinalIgnoreCase), "CMD cd updates the visible effective working directory");

            await service.ExecuteAsync(ps.Id, "Start-Sleep -Seconds 20", CancellationToken.None);
            var duplicateRejected = false;
            try { await service.ExecuteAsync(ps.Id, "Write-Output duplicate", CancellationToken.None); } catch (InvalidOperationException) { duplicateRejected = true; }
            var cancelled = service.Cancel(ps.Id);
            Check(duplicateRejected && cancelled.State == "cancelled" && cancelled.RunningCommand is null, "terminal rejects overlapping commands and cancellation terminates the active session");
            service.ClearOutput(cmd.Id);
            Check(service.Get(cmd.Id).State == "ready" && service.Get(cmd.Id).StandardOutput.Length == 0, "clearing terminal display preserves the live session");
            Check(!WorkspaceLayoutState.IsAvailable(Path.Combine(root, "missing")), "removed or invalid workspace becomes unavailable immediately");
            Console.WriteLine(checks + " terminal checks passed.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<TerminalSessionSnapshot> WaitReadyAsync(TerminalSessionService service, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = service.Get(id);
            if (snapshot.State != "running") return snapshot;
            await Task.Delay(50);
        }
        var last = service.Get(id);
        throw new TimeoutException($"Terminal command did not complete in time. state={last.State}; out={last.StandardOutput}; err={last.StandardError}");
    }
}
