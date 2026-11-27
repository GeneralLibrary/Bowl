using System.Text.Json;
using GeneralUpdate.Bowl.Strategies;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     Owner of the crash-evidence outbox on the Bowl side:
///     <c>{install}/fail/{version}/{guid}/</c> with one <c>manifest.json</c> (schema 2) per case.
/// </summary>
internal sealed class OutboxManager
{
    internal const int MaxCasesPerVersion = 10;
    internal const int MaxCasesTotal = 100;

    private readonly WatchOptions _options;

    public OutboxManager(WatchOptions options) => _options = options;

    /// <summary>Root of all evidence: <c>{install}/fail</c>.</summary>
    public string FailRoot => Path.Combine(_options.InstallPath, "fail");

    /// <summary>Version directory of the currently watched application.</summary>
    public string VersionRoot => Path.Combine(FailRoot, _options.Version);

    /// <summary>
    ///     Creates the session (case) directory BEFORE the watcher attaches, so dump tools write
    ///     directly into their final outbox location and evidence never has to be moved across
    ///     directories (or volumes) after a crash.
    /// </summary>
    /// <returns>The created directory; its name is the case GUID (server idempotency key).</returns>
    public OutboxSession? CreateSession()
    {
        var locks = Path.Combine(FailRoot, ".locks");
        Directory.CreateDirectory(locks);
        using var admission = TryOpenLock(Path.Combine(locks, "admission.lock"));
        if (admission == null)
        {
            GeneralTracer.Warn("OutboxManager: admission lock busy; this session will run without evidence.");
            return null;
        }

        var versionCount = 0;
        var totalCount = 0;
        foreach (var version in Directory.EnumerateDirectories(FailRoot))
        {
            if (Path.GetFileName(version).Equals(".locks", StringComparison.OrdinalIgnoreCase)) continue;
            var count = Directory.EnumerateDirectories(version).Count();
            totalCount += count;
            if (string.Equals(Path.GetFullPath(version), Path.GetFullPath(VersionRoot),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                versionCount = count;
        }

        if (versionCount >= MaxCasesPerVersion || totalCount >= MaxCasesTotal)
        {
            GeneralTracer.Warn($"OutboxManager: evidence capacity reached (version={versionCount}/{MaxCasesPerVersion}, total={totalCount}/{MaxCasesTotal}); this session will run without evidence.");
            return null;
        }

        for (var i = 0; i < 256; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            var lease = TryOpenLock(Path.Combine(locks, id[..2] + ".lock"));
            if (lease == null) continue;
            var directory = Path.Combine(VersionRoot, id);
            try
            {
                Directory.CreateDirectory(directory);
                return new OutboxSession(directory, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        GeneralTracer.Warn("OutboxManager: no case lock available; this session will run without evidence.");
        return null;
    }

    private static FileStream? TryOpenLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex)
        {
            GeneralTracer.Warn($"OutboxManager: cannot acquire lock {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Writes <c>manifest.json</c> (schema 2) for a crashed session. Written last so the
    ///     directory becoming "visible as pending" implies every evidence file is already on disk.
    /// </summary>
    public void WriteManifest(string sessionDirectory, WatchOutcome outcome)
    {
        var files = Directory.Exists(sessionDirectory)
            ? Directory.GetFiles(sessionDirectory)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    && !n.Equals("manifest.json.tmp", StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .ToList()
            : new List<string>();

        var manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        var tempPath = manifestPath + ".tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema", WatchOptions.SupportedSchema);
                writer.WriteNumber("outboxProtocol", 1);
                writer.WriteString("guid", Path.GetFileName(sessionDirectory));
                writer.WriteString("kind", "crash");
                writer.WriteString("targetName", _options.TargetName);
                writer.WriteString("version", _options.Version);
                if (outcome.TargetExitCode is int exitCode)
                    writer.WriteNumber("exitCode", exitCode);
                else
                    writer.WriteNull("exitCode");
                writer.WriteString("createdAtUtc", DateTime.UtcNow);
                writer.WriteStartArray("files");
                foreach (var file in files)
                    writer.WriteStringValue(file);
                writer.WriteEndArray();
                writer.WriteString("state", "pending");
                writer.WriteNumber("attempts", 0);
                writer.WriteEndObject();
                // Flush to disk before the atomic rename: the manifest is what makes a
                // freshly captured crash case visible to the outbox sweep, and it is written
                // precisely when the machine is least stable (post-crash). A torn manifest
                // would orphan the case's dumps until GC deletes them. Mirrors Bowl.Host's
                // Disk.Write durability.
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        GeneralTracer.Info($"OutboxManager: manifest written ({files.Count} file(s)) -> {manifestPath}.");
    }

    /// <summary>
    ///     Removes a session directory left behind by a NORMAL exit — a normal exit produces no
    ///     evidence (§2.3), so the pre-created session directory must not linger as an empty
    ///     look-alike case that the Core scanner would trip over.
    /// </summary>
    public void RemoveSessionIfEmpty(string sessionDirectory)
    {
        try
        {
            if (!Directory.Exists(sessionDirectory)) return;
            if (Directory.EnumerateFileSystemEntries(sessionDirectory).Any()) return;
            Directory.Delete(sessionDirectory, false);
        }
        catch (Exception ex)
        {
            GeneralTracer.Warn($"OutboxManager: could not remove empty session directory: {ex.Message}");
        }
    }

    /// <summary>Report capacity pressure without deleting unacknowledged or incomplete evidence.</summary>
    public void CollectGarbage()
    {
        try
        {
            if (!Directory.Exists(FailRoot)) return;

            var total = 0;
            foreach (var versionDir in Directory.EnumerateDirectories(FailRoot))
            {
                if (Path.GetFileName(versionDir).Equals(".locks", StringComparison.OrdinalIgnoreCase)) continue;
                var count = Directory.EnumerateDirectories(versionDir).Count();
                total += count;
                if (count >= MaxCasesPerVersion)
                    GeneralTracer.Warn($"OutboxManager: {versionDir} contains {count} cases; evidence is retained and new sessions may be unable to capture evidence.");
            }
            if (total >= MaxCasesTotal)
                GeneralTracer.Warn($"OutboxManager: outbox contains {total} cases; evidence is retained and new sessions may be unable to capture evidence.");
        }
        catch (Exception ex)
        {
            GeneralTracer.Warn($"OutboxManager: GC pass failed: {ex.Message}");
        }
    }

    /// <summary>Graceful-stop signal polled by the supervisor loop (written by <c>bowl stop</c>).</summary>
    public bool IsStopRequested(string? generation) => ReadStopGeneration() != generation;

    public string? ReadStopGeneration()
    {
        try
        {
            using var stream = new FileStream(_options.StopSignalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Requests a graceful stop of the watchdog watching this bowl-home.</summary>
    public static void RequestStop(string bowlHome)
    {
        var path = Path.Combine(bowlHome, "stop.signal");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Guid.NewGuid().ToString("N"));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

}

internal sealed class OutboxSession : IDisposable
{
    private readonly FileStream _lease;
    public string Directory { get; }

    public OutboxSession(string directory, FileStream lease) => (Directory, _lease) = (directory, lease);
    public void Dispose() => _lease.Dispose();
}
