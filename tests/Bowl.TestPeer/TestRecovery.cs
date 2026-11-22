using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Bowl.Host;

// Test-only adapter; its directory checks are not a production authorization boundary.
internal static class TestRecovery
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length != 4 && args.Length != 7 ||
                args.Length == 7 && (args[4] != "--pause" ||
                    (!int.TryParse(args[5], out var pauseIndex) || pauseIndex < 0) ||
                    args[6] is not ("intent" or "applied" or "complete")))
                throw new InvalidDataException("Usage: test-recover <test-root> <attempt> <plan-path> [--pause <index> <intent|applied|complete>].");

            var root = Paths.Absolute(args[1]);
            var allowed = Paths.Absolute(Path.Combine(Path.GetTempPath(), "BowlControlledIntegration"));
            if (!Guid.TryParseExact(Path.GetFileName(root), "D", out var rootId) ||
                rootId.ToString("D") != Path.GetFileName(root) ||
                args[2] != rootId.ToString("D") ||
                !string.Equals(Path.GetDirectoryName(root), allowed,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Root must be %TEMP%/BowlControlledIntegration/<canonical-guid>.");
            var install = Paths.Absolute(Path.Combine(root, "install"));
            var backup = Paths.Absolute(Path.Combine(root, "backup"));
            var state = Paths.Absolute(Path.Combine(root, "state"));
            var stage = Paths.Absolute(Path.Combine(root, "stage"));
            var plan = Paths.Absolute(args[3]);
            var planName = Path.GetFileName(plan);
            if (!EqualPath(Path.GetDirectoryName(plan)!, state) ||
                !planName.StartsWith("apply-", StringComparison.Ordinal) ||
                !planName.EndsWith(".json", StringComparison.Ordinal) ||
                !Guid.TryParseExact(planName.Substring(6, planName.Length - 11), "N", out var planId) ||
                planName != $"apply-{planId:N}.json")
                throw new InvalidDataException("Plan must be state/apply-<32 lowercase hex>.json.");
            if (!Directory.Exists(install) || !Directory.Exists(backup) || !Directory.Exists(state) ||
                !Directory.Exists(stage))
                throw new InvalidDataException("Test install, backup, stage and state directories must already exist.");

            var lockPath = Path.Combine(state, "recovery.lock");
            Paths.NoLinks(lockPath);
            using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var (entries, planHash) = ReadPlan(plan, install, backup, state, stage);
            var journalPath = Path.Combine(state, "recovery-journal.json");
            Paths.NoLinks(journalPath);
            var bindingPath = Path.Combine(state, "plan-sha256");
            Paths.NoLinks(bindingPath);
            if (!File.Exists(bindingPath))
            {
                if (File.Exists(journalPath))
                    throw new InvalidDataException("Journal has no plan binding.");
                using var binding = new FileStream(bindingPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough);
                binding.Write(Encoding.ASCII.GetBytes(planHash));
                binding.Flush(true);
            }
            else
            {
                using var binding = new FileStream(bindingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (binding.Length != 64 || new StreamReader(binding, Encoding.ASCII).ReadToEnd() != planHash)
                    throw new InvalidDataException("Immutable plan binding mismatch.");
            }
            if (!File.Exists(journalPath))
                ControlledJournal.Create(journalPath, args[2], entries);
            var journal = ControlledJournal.Read(journalPath, args[2]);
            if (journal.Entries.Length != entries.Length ||
                !journal.Entries.Zip(entries).All(pair =>
                    pair.First.Path == pair.Second.Path &&
                    pair.First.OriginalSha256 == pair.Second.OriginalSha256 &&
                    pair.First.UpdatedSha256 == pair.Second.UpdatedSha256))
                throw new InvalidDataException("Journal does not match the immutable plan.");

            ControlledRecovery.Restore(install, backup, journalPath, args[2], (index, phase) =>
            {
                if (args.Length == 7 && index == int.Parse(args[5]) && phase == args[6])
                {
                    Console.WriteLine($"CHECKPOINT {index} {phase}");
                    Console.Out.Flush();
                    Thread.Sleep(Timeout.Infinite);
                }
            });
            Console.WriteLine("RECOVERY_COMPLETE");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or JsonException or ArgumentException or
                                   NotSupportedException)
        {
            await Console.Error.WriteLineAsync($"RECOVERY_FAILED: {ex.Message}");
            return 2;
        }
    }

    private static (JournalEntry[] Entries, string Hash) ReadPlan(
        string path, string install, string backup, string state, string stage)
    {
        Paths.NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > Disk.MaximumRecordBytes)
            throw new InvalidDataException("Plan exceeds 1 MiB.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var planBytes = bytes.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant();
        using var document = JsonDocument.Parse(planBytes);
        var root = document.RootElement;
        Properties(root, "version", "targetRoot", "stateDirectory", "protectedPaths", "entries");
        if (root.GetProperty("version").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
            !EqualPath(String(root, "targetRoot"), install) ||
            !EqualPath(String(root, "stateDirectory"), state) ||
            root.GetProperty("protectedPaths").ValueKind != JsonValueKind.Array ||
            root.GetProperty("entries").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid plan roots or version.");
        var protectedPaths = new List<string>();
        foreach (var item in root.GetProperty("protectedPaths").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid protected path.");
            protectedPaths.Add(RelativePath(install, item.GetString()!));
        }
        if (!protectedPaths.Any(p => EqualPath(p, Path.Combine(install, ".backups"))) ||
            protectedPaths.Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != protectedPaths.Count)
            throw new InvalidDataException("Invalid protected paths.");
        var array = root.GetProperty("entries");
        if (array.GetArrayLength() is < 1 or > 10_000)
            throw new InvalidDataException("Invalid plan size.");
        var entries = new List<JournalEntry>();
        foreach (var item in array.EnumerateArray())
        {
            Properties(item, "path", "targetPath", "stagedPath", "updatedSha256",
                "snapshotPath", "originalSha256", "temporaryPath");
            var original = item.GetProperty("originalSha256");
            var snapshot = item.GetProperty("snapshotPath");
            if (original.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                snapshot.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Invalid plan entry.");
            var relative = String(item, "path");
            var target = RelativePath(install, relative);
            if (!EqualPath(String(item, "targetPath"), target) ||
                protectedPaths.Any(protectedPath => Paths.Overlaps(protectedPath, target)))
                throw new InvalidDataException("Invalid or protected target.");
            var staged = Paths.Absolute(String(item, "stagedPath"));
            var temporary = Paths.Absolute(String(item, "temporaryPath"));
            var temporaryName = Path.GetFileName(temporary);
            if (!Paths.Contains(stage, staged) || EqualPath(staged, stage) ||
                !EqualPath(Path.GetDirectoryName(temporary)!, Path.GetDirectoryName(target)!) ||
                !temporaryName.StartsWith(".gu-apply-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(temporaryName.Substring(10), "N", out var temporaryId) ||
                temporaryName != $".gu-apply-{temporaryId:N}" ||
                EqualPath(temporary, target) ||
                protectedPaths.Any(protectedPath => Paths.Overlaps(protectedPath, temporary)))
                throw new InvalidDataException("Invalid staged or temporary path.");
            if (original.ValueKind == JsonValueKind.Null ? snapshot.ValueKind != JsonValueKind.Null :
                snapshot.ValueKind != JsonValueKind.String ||
                !EqualPath(snapshot.GetString()!, RelativePath(backup, relative)))
                throw new InvalidDataException("Invalid snapshot path.");
            entries.Add(new JournalEntry(relative, original.GetString(), String(item, "updatedSha256")));
        }
        return (entries.ToArray(), hash);
    }

    private static string String(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
            throw new InvalidDataException("Expected a non-empty string.");
        return value.GetString()!;
    }

    private static string RelativePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(part =>
                part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid relative path.");
        var path = Paths.Absolute(Path.Combine(root, Path.Combine(relative.Split(['/', '\\']))));
        if (!Paths.Contains(root, path) || EqualPath(root, path))
            throw new InvalidDataException("Path escapes test root.");
        return path;
    }

    private static bool EqualPath(string a, string b) =>
        string.Equals(Paths.Absolute(a), Paths.Absolute(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Properties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Plan object expected.");
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected, missing or duplicate plan property.");
    }
}
