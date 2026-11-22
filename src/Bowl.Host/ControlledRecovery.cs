using System.Security.Cryptography;

namespace Bowl.Host;

// Unwired recovery algorithm for a trusted, isolated test environment. A
// caller must provide an exclusive trusted tree and an authenticated journal.
internal static class ControlledRecovery
{
    public static void Restore(string install, string backup, string journalPath, string attempt,
        Action<int, string>? checkpoint = null)
    {
        install = Paths.Absolute(install);
        backup = Paths.Absolute(backup);
        journalPath = Paths.Absolute(journalPath);
        if (Paths.Overlaps(install, backup) || Paths.Contains(install, journalPath) ||
            Paths.Contains(backup, journalPath))
            throw new InvalidDataException("Recovery paths must be disjoint.");
        var journal = ControlledJournal.Read(journalPath, attempt);
        var targets = journal.Entries.Select(e => Resolve(install, e.Path)).ToArray();
        var sources = journal.Entries.Select(e => Resolve(backup, e.Path)).ToArray();
        for (var i = 0; i < journal.Entries.Length; i++)
            Check(journal.Entries[i], targets[i], sources[i]);

        for (var i = 0; i < journal.Entries.Length; i++)
        {
            var entry = ControlledJournal.Read(journalPath, attempt).Entries[i];
            Check(entry, targets[i], sources[i]);
            if (entry.State == "complete")
                continue;
            if (entry.State == "pending")
            {
                ControlledJournal.Advance(journalPath, attempt, i, "inProgress");
                checkpoint?.Invoke(i, "intent");
            }
            entry = ControlledJournal.Read(journalPath, attempt).Entries[i];
            var actual = Check(entry, targets[i], sources[i]);
            if (entry.OriginalSha256 == null)
            {
                if (actual == entry.UpdatedSha256 && actual != null)
                    File.Delete(targets[i]);
            }
            else if (actual != entry.OriginalSha256)
            {
                AtomicRestore(sources[i], targets[i], entry.OriginalSha256, entry.UpdatedSha256);
            }
            checkpoint?.Invoke(i, "applied");
            ControlledJournal.Advance(journalPath, attempt, i, "complete");
            checkpoint?.Invoke(i, "complete");
        }
    }

    private static string? Check(JournalEntry entry, string target, string source)
    {
        Paths.NoLinks(target);
        Paths.NoLinks(source);
        if (Directory.Exists(target) || Directory.Exists(source))
            throw new InvalidDataException("A recovery file is a directory.");
        var actual = HashFile(target);
        var snapshot = HashFile(source);
        if (entry.OriginalSha256 == null)
        {
            if (snapshot != null || actual != null && actual != entry.UpdatedSha256 ||
                entry.State == "complete" && actual != null)
                throw new InvalidDataException("New-file recovery conflict.");
        }
        else if (snapshot != entry.OriginalSha256 ||
                 actual != entry.OriginalSha256 && actual != entry.UpdatedSha256 ||
                 entry.State == "complete" && actual != entry.OriginalSha256)
            throw new InvalidDataException("Existing-file recovery conflict.");
        return actual;
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void AtomicRestore(string source, string target, string expectedHash, string updatedHash)
    {
        var parent = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(parent, ".bowl-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (HashFile(temp) != expectedHash)
                throw new InvalidDataException("Snapshot changed during recovery.");
            Paths.NoLinks(target);
            if (HashFile(target) != updatedHash)
                throw new InvalidDataException("Installation changed during recovery.");
            File.Replace(temp, target, null);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static string Resolve(string root, string relative)
    {
        var parts = relative.Split(['/', '\\']);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!Paths.Contains(root, path) || string.Equals(root, path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Paths.Contains(Path.Combine(root, ".backups"), path))
            throw new InvalidDataException("Unsafe recovery path.");
        return path;
    }
}
