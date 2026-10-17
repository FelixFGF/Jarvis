namespace Jarvis.Update.Core;

public static class UpdateFileTransaction
{
    public static IReadOnlyList<string> Apply(string sourceRoot, string installRoot, string backupRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot); installRoot = Path.GetFullPath(installRoot); backupRoot = Path.GetFullPath(backupRoot);
        var changed = new List<string>();
        try
        {
            foreach (var source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, source);
                if (relative.Equals("JarvisData", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("JarvisData" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
                if (!UpdateSecurity.IsSafeArchivePath(normalized)) throw new InvalidDataException("Unsafe update path.");
                var target = Path.GetFullPath(Path.Combine(installRoot, relative));
                if (!target.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes the installation folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, true);
                }
                changed.Add(relative);
                File.Copy(source, target, true);
            }
            return changed;
        }
        catch
        {
            Restore(changed, installRoot, backupRoot);
            throw;
        }
    }

    public static void Restore(IEnumerable<string> changedFiles, string installRoot, string backupRoot)
    {
        installRoot = Path.GetFullPath(installRoot); backupRoot = Path.GetFullPath(backupRoot);
        foreach (var relative in changedFiles.Reverse())
        {
            var target = Path.Combine(installRoot, relative); var backup = Path.Combine(backupRoot, relative);
            if (File.Exists(backup)) File.Copy(backup, target, true); else if (File.Exists(target)) File.Delete(target);
        }
    }
}
