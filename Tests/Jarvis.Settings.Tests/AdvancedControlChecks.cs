using Jarvis.Services;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

internal static class AdvancedControlChecks
{
    public static void Run()
    {
        var checks = 0; void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
        Check(!ToolPermissionPolicy.IsHardBoundary("Open visible PowerShell", "Working directory C:\\Work") && ToolPermissionPolicy.IsHardBoundary("Delete files", "Remove output"), "risk classification still distinguishes routine and destructive actions for warnings and audit data");
        Check(ToolPermissionPolicy.CanRunWithoutPrompt("allow_eligible", "Allow Jarvis to view the screen for Computer Use?", "Do not enter passwords or tokens.", "computer-use-session")
            && ToolPermissionPolicy.CanRunWithoutPrompt("allow_eligible", "Run terminal command?", "Get-ChildItem", "terminal:powershell:C:\\Work:Get-ChildItem")
            && !ToolPermissionPolicy.CanRunWithoutPrompt("ask", "Run terminal command?", "Get-ChildItem")
            && ToolPermissionPolicy.CanRunWithoutPrompt("allow_eligible", "Delete file?", "Remove-Item important.txt"),
            "Allow Everything applies immediately to all supported actions including deletion");
        Check(ToolPermissionPolicy.Options(false).Single(option => option.Id == "allow_eligible").Label.StartsWith("⚠", StringComparison.Ordinal)
            && ToolPermissionPolicy.Options(true).Single(option => option.Id == "allow_eligible").ToolTip.Contains("Löschen", StringComparison.Ordinal),
            "Allow Everything carries an explicit warning in English and German");
        var tracker = new FileChangeTrackingService();
        var change = tracker.Record("task", Path.Combine(Path.GetTempPath(), "sample.txt"), FileChangeKind.Modified, "test", "one\ntwo\n", "one\nthree\nfour\n", null);
        Check(change.AddedLines == 2 && change.RemovedLines == 1 && tracker.Snapshot().Single().Id == change.Id, "file-change tracking calculates real added and removed lines");
        var diff = FileChangeTrackingService.BuildDiff("alpha\nbeta", "alpha\ngamma");
        Check(diff.Count(line => line.Kind == DiffLineKind.Added) == 1 && diff.Count(line => line.Kind == DiffLineKind.Removed) == 1, "line diff preserves context and reports exact additions and removals");
        var aggregateTracker = new FileChangeTrackingService(); var changeEvents = 0;
        aggregateTracker.Changed += (_, _) => changeEvents++;
        var aggregatePath = Path.Combine(Path.GetTempPath(), "aggregate.txt");
        aggregateTracker.Record("aggregate-task", aggregatePath, FileChangeKind.Modified, "first", "one\ntwo", "one\nthree", null);
        aggregateTracker.Record("aggregate-task", aggregatePath, FileChangeKind.Modified, "second", "one\nthree", "one\nthree\nfour", null);
        var aggregate = FileChangeTrackingService.SummarizeByPath(aggregateTracker.Snapshot());
        Check(changeEvents == 2 && aggregate.Count == 1 && aggregate[0].AddedLines == 2 && aggregate[0].RemovedLines == 1,
            "composer change summary updates from real records and calculates the net per-file diff");
        Check(new[] { ".py", ".java", ".js", ".ts", ".cs", ".c", ".cpp", ".html", ".css", ".scss", ".json", ".xml", ".yaml", ".md", ".ps1", ".sql", ".properties", ".ini", ".sh" }
            .All(extension => SyntaxLanguageCatalog.DefinitionFor("file" + extension) is not null), "workspace syntax catalog covers all required source and configuration languages");
        var mainMarkup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var workspaceMarkup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WorkspaceView.xaml"));
        var terminalMarkup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TerminalPanel.xaml"));
        var workspaceSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.WorkspaceTerminal.cs"));
        Check(mainMarkup.Contains("x:Name=\"WorkspaceToggleButton\"", StringComparison.Ordinal) && mainMarkup.Contains("x:Name=\"ChatTerminalPanel\"", StringComparison.Ordinal) &&
            workspaceMarkup.Contains("x:Name=\"ProjectRootBox\"", StringComparison.Ordinal) && workspaceMarkup.Contains("x:Name=\"WorkspaceTerminalPanel\"", StringComparison.Ordinal) && workspaceMarkup.Contains("x:Name=\"TerminalTab\"", StringComparison.Ordinal),
            "workspace toggle, project selector and independent chat/workspace terminal locations are wired into the active XAML");
        Check(workspaceMarkup.Contains("x:Name=\"ClearRootButton\"", StringComparison.Ordinal)
            && terminalMarkup.Contains("PreviewKeyDown=\"CommandBox_PreviewKeyDown\"", StringComparison.Ordinal)
            && terminalMarkup.Contains("x:Name=\"PromptLabel\"", StringComparison.Ordinal)
            && !terminalMarkup.Contains("Click=\"Run_Click\"", StringComparison.Ordinal),
            "workspace clear action and Enter-first PowerShell-style terminal UI are wired into the active XAML");
        Check(workspaceSource.Contains("UpdateWorkspaceAvailability(root)", StringComparison.Ordinal)
            && workspaceSource.Contains("CommandManager.InvalidateRequerySuggested()", StringComparison.Ordinal)
            && workspaceSource.Contains("IntegratedWorkspace.ProjectRoot ?? _activeChat?.Workspace", StringComparison.Ordinal),
            "workspace selection invalidates command state immediately without requiring a chat switch");
        var changeStore = Path.Combine(AppContext.BaseDirectory, "jarvis-file-changes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var persisted = new FileChangeTrackingService(); persisted.ConfigurePersistence(changeStore);
            persisted.Record("task-persisted", Path.Combine(Path.GetTempPath(), "created.txt"), FileChangeKind.Added, "created for test", null, "hello", null);
            Check(File.Exists(changeStore), "file-change history writes its persistence file atomically: " + persisted.LastPersistenceError);
            var restored = new FileChangeTrackingService(); restored.ConfigurePersistence(changeStore);
            Check(restored.Snapshot().Count == 1 && restored.Snapshot()[0].TaskId == "task-persisted", "file-change history persists and restores from the per-user store");
        }
        finally { if (File.Exists(changeStore)) File.Delete(changeStore); }
        var rollbackRoot = Path.Combine(AppContext.BaseDirectory, "jarvis-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(rollbackRoot);
            var createdPath = Path.Combine(rollbackRoot, "created.txt"); File.WriteAllText(createdPath, "created");
            var rollbackTracker = new FileChangeTrackingService();
            var created = rollbackTracker.Record("task", createdPath, FileChangeKind.Added, "test create", null, "created");
            var rolledBack = rollbackTracker.RollbackAsync(created.Id).GetAwaiter().GetResult();
            Check(rolledBack.Success && !File.Exists(createdPath), "safe rollback removes an unchanged file created by Jarvis");
            var guardedPath = Path.Combine(rollbackRoot, "guarded.txt"); File.WriteAllText(guardedPath, "initial");
            var guarded = rollbackTracker.Record("task", guardedPath, FileChangeKind.Added, "test create", null, "initial");
            File.WriteAllText(guardedPath, "newer user work");
            var refused = rollbackTracker.RollbackAsync(guarded.Id).GetAwaiter().GetResult();
            Check(!refused.Success && File.ReadAllText(guardedPath) == "newer user work", "rollback refuses to overwrite newer external work");
        }
        finally { if (Directory.Exists(rollbackRoot)) Directory.Delete(rollbackRoot, true); }
        using (var json = System.Text.Json.JsonDocument.Parse("""[{"project_id":"p","id":"v","name":"Compatible","version_number":"1.2.3","game_versions":["1.21.1"],"loaders":["fabric"],"dependencies":[{"project_id":"fabric-api"}],"files":[{"primary":true,"url":"https://cdn.modrinth.com/data/p/versions/v/mod.jar","filename":"mod.jar","size":42,"hashes":{"sha512":"abc"}}]},{"project_id":"p","id":"wrong","name":"Wrong loader","version_number":"2","game_versions":["1.21.1"],"loaders":["forge"],"files":[]} ]"""))
        {
            var versions = ModrinthApiService.ParseVersions(json.RootElement, "1.21.1", "fabric");
            Check(versions.Count == 1 && versions[0].VersionId == "v" && versions[0].Dependencies.Single() == "fabric-api" && versions[0].DownloadUrl.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal), "Modrinth version filtering preserves exact loader, game version, dependency, HTTPS file and hash metadata");
        }
        var downloadRoot = Path.Combine(AppContext.BaseDirectory, "jarvis-modrinth-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] jar;
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open())) writer.Write("{}");
                jar = memory.ToArray();
            }
            var sha = Convert.ToHexString(SHA512.HashData(jar));
            using var http = new HttpClient(new StaticHttpHandler(jar));
            using var modrinth = new ModrinthApiService(http);
            var downloaded = modrinth.DownloadVerifiedAsync(new ModrinthVersion("p", "v", "Fixture", "1", ["1.21.1"], ["fabric"], [], "https://cdn.modrinth.com/data/p/v/mod.jar", "mod.jar", sha, jar.Length), downloadRoot, CancellationToken.None).GetAwaiter().GetResult();
            Check(File.Exists(downloaded) && SHA512.HashData(File.ReadAllBytes(downloaded)).SequenceEqual(SHA512.HashData(jar)), "Modrinth download verifies SHA-512 and recognizable mod JAR structure before installation");
        }
        finally { if (Directory.Exists(downloadRoot)) Directory.Delete(downloadRoot, true); }
        var root = Path.Combine(Path.GetTempPath(), "jarvis-minecraft-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instance = Path.Combine(root, "instances", "Test", ".minecraft"); Directory.CreateDirectory(Path.Combine(instance, "logs")); Directory.CreateDirectory(Path.Combine(instance, "mods"));
            File.WriteAllText(Path.Combine(instance, "logs", "latest.log"), "java.lang.UnsupportedClassVersionError: class file version 65");
            File.WriteAllText(Path.Combine(instance, "mods", "example-1.0.0.jar"), "fixture"); File.WriteAllText(Path.Combine(instance, "mods", "example-2.0.0.jar"), "fixture");
            var discovered = MinecraftDiagnosisService.Discover([root]); var diagnosis = MinecraftDiagnosisService.Diagnose(discovered.Single());
            Check(discovered.Single().Root == instance && diagnosis.Findings.Any(item => item.Code == "java-version") && diagnosis.Findings.Any(item => item.Code == "duplicate-mod"), "Minecraft discovery uses launcher instance paths and evidence-based crash signatures");
            var backup = MinecraftDiagnosisService.CreateBackup(instance, [Path.Combine(instance, "mods", "example-1.0.0.jar")], Path.Combine(root, "backups"));
            Check(File.Exists(backup), "Minecraft repair backup is created before a controlled file change");
            try { MinecraftDiagnosisService.CreateBackup(instance, [Path.Combine(root, "outside.txt")], Path.Combine(root, "backups")); throw new Exception("outside backup accepted"); } catch (UnauthorizedAccessException) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine(checks + " advanced control checks passed.");
    }
}

internal sealed class StaticHttpHandler(byte[] content) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
}
