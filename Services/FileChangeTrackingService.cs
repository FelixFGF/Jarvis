using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Jarvis.Services;

public enum FileChangeKind { Added, Modified, Removed, Moved, FolderCreated, FolderRemoved, Renamed }
public enum DiffLineKind { Context, Added, Removed }
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLine, int? NewLine);
public sealed record FileChangeRecord(
    string Id, string TaskId, string Path, FileChangeKind Kind, string Reason,
    DateTimeOffset Timestamp, string? BackupPath, int AddedLines, int RemovedLines,
    bool Applied, bool RollbackAvailable, string? BeforeText = null, string? AfterText = null,
    string? PreviousPath = null, bool IsBinary = false);

public sealed class FileChangeTrackingService
{
    private const int SnapshotLimit = 1_048_576;
    private readonly ConcurrentQueue<FileChangeRecord> _changes = new();
    private string? _storePath;
    public string? LastPersistenceError { get; private set; }
    public event EventHandler? Changed;
    public IReadOnlyList<FileChangeRecord> Snapshot() => _changes.ToArray();

    public void ConfigurePersistence(string storePath)
    {
        _storePath = Path.GetFullPath(storePath);
        if (!File.Exists(_storePath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<FileChangeRecord>>(File.ReadAllText(_storePath)) ?? [];
            foreach (var item in saved.OrderBy(item => item.Timestamp).TakeLast(2000)) _changes.Enqueue(item);
        }
        catch (JsonException) { }
    }

    public FileChangeRecord Record(string taskId, string path, FileChangeKind kind, string reason, string? before, string? after, string? backup = null, string? previousPath = null)
    {
        var isBinary = LooksBinary(before) || LooksBinary(after) || new[] { ".jar", ".zip", ".png", ".jpg", ".jpeg", ".dll", ".exe" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        var (added, removed) = isBinary ? (0, 0) : CountChangedLines(before, after);
        var beforeSnapshot = SnapshotText(before);
        var afterSnapshot = SnapshotText(after);
        var rollback = kind switch
        {
            FileChangeKind.Added => afterSnapshot is not null,
            FileChangeKind.FolderCreated => true,
            FileChangeKind.Modified => afterSnapshot is not null && !string.IsNullOrWhiteSpace(backup) && File.Exists(backup),
            FileChangeKind.Removed => !string.IsNullOrWhiteSpace(backup) && File.Exists(backup),
            FileChangeKind.Moved or FileChangeKind.Renamed => !string.IsNullOrWhiteSpace(previousPath),
            _ => false
        };
        var item = new FileChangeRecord(Guid.NewGuid().ToString("N"), taskId, Path.GetFullPath(path), kind,
            reason.Trim(), DateTimeOffset.Now, backup, added, removed, true, rollback,
            beforeSnapshot, afterSnapshot, previousPath is null ? null : Path.GetFullPath(previousPath), isBinary);
        _changes.Enqueue(item);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public async Task<(bool Success, string Message)> RollbackAsync(string changeId, CancellationToken token = default)
    {
        var change = _changes.LastOrDefault(item => item.Id == changeId);
        if (change is null || !change.RollbackAvailable) return (false, "Rollback is not available for this change.");
        token.ThrowIfCancellationRequested();
        try
        {
            switch (change.Kind)
            {
                case FileChangeKind.Added:
                    if (!File.Exists(change.Path)) return (false, "The created file no longer exists.");
                    if (change.AfterText is not null && !string.Equals(await File.ReadAllTextAsync(change.Path, token), change.AfterText, StringComparison.Ordinal))
                        return (false, "The file changed after Jarvis created it. Rollback was stopped to protect newer work.");
                    File.Delete(change.Path);
                    break;
                case FileChangeKind.FolderCreated:
                    if (!Directory.Exists(change.Path)) return (false, "The created folder no longer exists.");
                    if (Directory.EnumerateFileSystemEntries(change.Path).Any()) return (false, "The folder is no longer empty. Rollback was stopped to protect its contents.");
                    Directory.Delete(change.Path);
                    break;
                case FileChangeKind.Modified:
                case FileChangeKind.Removed:
                    if (string.IsNullOrWhiteSpace(change.BackupPath) || !File.Exists(change.BackupPath)) return (false, "The verified backup is unavailable.");
                    if (change.Kind == FileChangeKind.Removed && File.Exists(change.Path))
                        return (false, "A new file now exists at the removed path. Rollback was stopped to protect it.");
                    if (change.Kind == FileChangeKind.Modified && File.Exists(change.Path) && change.AfterText is not null &&
                        !string.Equals(await File.ReadAllTextAsync(change.Path, token), change.AfterText, StringComparison.Ordinal))
                        return (false, "The file changed after the recorded operation. Rollback was stopped.");
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                    File.Copy(change.BackupPath, change.Path, true);
                    break;
                case FileChangeKind.Moved:
                case FileChangeKind.Renamed:
                    if (string.IsNullOrWhiteSpace(change.PreviousPath) || !File.Exists(change.Path) || File.Exists(change.PreviousPath))
                        return (false, "The move can no longer be reversed safely.");
                    Directory.CreateDirectory(Path.GetDirectoryName(change.PreviousPath)!);
                    File.Move(change.Path, change.PreviousPath);
                    break;
                default:
                    return (false, "This operation has no safe rollback implementation.");
            }
            Replace(change with { Applied = false, RollbackAvailable = false });
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
            return (true, "Rollback completed and verified.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }

    public static IReadOnlyList<DiffLine> BuildDiff(string? before, string? after)
    {
        var left = Split(before); var right = Split(after);
        if ((long)left.Length * right.Length > 4_000_000)
            return [new(DiffLineKind.Context, "Diff is too large for an inline preview.", null, null)];
        var table = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
            for (var j = right.Length - 1; j >= 0; j--)
                table[i, j] = left[i] == right[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        var result = new List<DiffLine>(); var x = 0; var y = 0; var oldLine = 1; var newLine = 1;
        while (x < left.Length || y < right.Length)
        {
            if (x < left.Length && y < right.Length && left[x] == right[y])
            { result.Add(new(DiffLineKind.Context, left[x], oldLine++, newLine++)); x++; y++; }
            else if (y < right.Length && (x == left.Length || table[x, y + 1] >= table[x + 1, y]))
            { result.Add(new(DiffLineKind.Added, right[y++], null, newLine++)); }
            else
            { result.Add(new(DiffLineKind.Removed, left[x++], oldLine++, null)); }
        }
        return result;
    }

    public static (int Added, int Removed) CountChangedLines(string? before, string? after)
    {
        var diff = BuildDiff(before, after);
        return (diff.Count(line => line.Kind == DiffLineKind.Added), diff.Count(line => line.Kind == DiffLineKind.Removed));
    }

    public static IReadOnlyList<FileChangeRecord> SummarizeByPath(IEnumerable<FileChangeRecord> changes)
    {
        return changes
            .Where(change => change.Applied)
            .GroupBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(change => change.Timestamp).ToArray();
                var first = ordered[0];
                var last = ordered[^1];
                var canCalculateNetTextDiff = !ordered.Any(change => change.IsBinary) &&
                    (first.BeforeText is not null || last.AfterText is not null);
                var totals = canCalculateNetTextDiff
                    ? CountChangedLines(first.BeforeText, last.AfterText)
                    : (Added: ordered.Sum(change => change.AddedLines), Removed: ordered.Sum(change => change.RemovedLines));
                return last with { AddedLines = totals.Added, RemovedLines = totals.Removed };
            })
            .OrderBy(change => change.Timestamp)
            .ToArray();
    }

    private void Replace(FileChangeRecord replacement)
    {
        var all = _changes.ToArray().Select(item => item.Id == replacement.Id ? replacement : item).ToArray();
        while (_changes.TryDequeue(out _)) { }
        foreach (var item in all) _changes.Enqueue(item);
    }
    private static bool LooksBinary(string? value) => value is not null && value.IndexOf('\0') >= 0;
    private static string? SnapshotText(string? value) => value is null || Encoding.UTF8.GetByteCount(value) > SnapshotLimit ? null : value;
    private static string[] Split(string? text) => text is null ? [] : text.Replace("\r\n", "\n").Split('\n');
    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_storePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var temp = _storePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_changes.ToArray().TakeLast(2000)));
            if (File.Exists(_storePath)) File.Replace(temp, _storePath, null);
            else File.Move(temp, _storePath);
            LastPersistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastPersistenceError = ex.Message;
        }
    }
}
