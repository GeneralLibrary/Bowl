namespace Bowl.Host;

internal static class Recovery
{
    private const int MaximumEntries = 100_000;

    // This routine is only called after the updater is gone and the identified
    // application is stopped. The caller journals "inProgress" before entry so
    // interrupted copies can be repeated from the same immutable snapshot.
    public static void Restore(string backupDirectory, string installPath)
    {
        var backup = Paths.Absolute(backupDirectory);
        var install = Paths.Absolute(installPath);
        if (Paths.Contains(backup, install))
            throw new InvalidDataException("Backup cannot contain installation.");
        if (!Directory.Exists(backup))
            throw new DirectoryNotFoundException("Rollback snapshot is missing.");
        var entries = EnumerateSafe(backup).ToArray();
        if (entries.Length == 0)
            throw new InvalidDataException("Refusing an empty rollback snapshot.");
        if (entries.Any(entry => Paths.Contains(Path.Combine(backup, ".backups"), entry)))
            throw new InvalidDataException("Snapshot must not include recursive .backups.");

        // Validate the whole installation before any destructive operation.
        // .backups stays untouched, including when the active snapshot is nested.
        Directory.CreateDirectory(install);
        var existing = EnumerateSafe(install, excludeBackups: true).ToArray();
        foreach (var path in existing.Reverse())
        {
            Paths.NoLinks(path);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: false);
            else
                File.Delete(path);
        }
        foreach (var path in entries)
        {
            Paths.NoLinks(path);
            var destination = Path.Combine(install, Path.GetRelativePath(backup, path));
            Paths.NoLinks(destination);
            if (Directory.Exists(path))
                Directory.CreateDirectory(destination);
            else
                File.Copy(path, destination, overwrite: true);
        }
    }

    private static IEnumerable<string> EnumerateSafe(string directory, bool excludeBackups = false)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var count = 0;
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            Paths.NoLinks(parent);
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (excludeBackups && Paths.Contains(Path.Combine(directory, ".backups"), path))
                    continue;
                Paths.NoLinks(path);
                if (++count > MaximumEntries)
                    throw new InvalidDataException("Recovery tree exceeds 100000 entries.");
                yield return path;
                if (Directory.Exists(path))
                    pending.Push(path);
            }
        }
    }
}
