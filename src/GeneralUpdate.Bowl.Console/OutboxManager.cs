using System.Text.Json;
using GeneralUpdate.Bowl.Strategies;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     Owner of the crash-evidence outbox on the Bowl side:
///     <c>{install}/fail/{version}/{guid}/</c> with one <c>manifest.json</c> (schema 2) per case.
/// </summary>
/// <remarks>
///     <para>
///         The disk contract with GeneralUpdate.Core (§3.2) needs zero coordination:
///     </para>
///     <list type="number">
///         <item>existing means pending — the watchdog only WRITES the case directory, Core only READS it;</item>
///         <item>uploaded means deleted — Core removes the whole directory after a successful upload;</item>
///         <item>schema tolerance — Core skips manifests whose schema is not 2, so the two
///             components evolve independently.</item>
///     </list>
///     <para>
///         Because the watchdog is the only long-lived resident component, it also owns the disk
///         GC (§3.3): the fail directory used to grow without bound ("EnsureDirectory must not
///         delete"), which is safe now that retention rules are explicit — 14 days per case,
///         10 cases per version, giveup-marked cases age out together with everything else.
///     </para>
/// </remarks>
internal sealed class OutboxManager
{
    /// <summary>Maximum cases kept per version directory (§3.3).</summary>
    internal const int MaxCasesPerVersion = 10;

    /// <summary>Maximum age of a case directory (§3.3).</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);

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
    public string CreateSessionDirectory()
    {
        var directory = Path.Combine(VersionRoot, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        return directory;
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
                .Where(n => n != null && !n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .ToList()
            : new List<string>();

        var manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        var tempPath = manifestPath + ".tmp";
        using (var stream = File.Create(tempPath))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", WatchOptions.SupportedSchema);
            writer.WriteString("guid", Path.GetFileName(sessionDirectory));
            writer.WriteString("kind", "crash");
            writer.WriteString("targetName", _options.TargetName);
            writer.WriteString("version", _options.Version);
            writer.WriteNumber("exitCode", outcome.TargetExitCode);
            writer.WriteString("createdAtUtc", DateTime.UtcNow);
            writer.WriteStartArray("files");
            foreach (var file in files)
                writer.WriteStringValue(file);
            writer.WriteEndArray();
            writer.WriteString("state", "pending");
            writer.WriteNumber("attempts", 0);
            writer.WriteEndObject();
        }

        File.Move(tempPath, manifestPath, overwrite: true);
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
            TryDeleteVersionRootIfEmpty();
        }
        catch (Exception ex)
        {
            GeneralTracer.Warn($"OutboxManager: could not remove empty session directory: {ex.Message}");
        }
    }

    /// <summary>
    ///     Disk GC (§3.3): cases older than <see cref="Retention"/> are removed (giveup-marked
    ///     ones included — Core only stops UPLOADING those, disposal stays Bowl's job), and each
    ///     version directory keeps at most <see cref="MaxCasesPerVersion"/> newest cases.
    ///     Every error is contained: GC must never take the watchdog down.
    /// </summary>
    public void CollectGarbage()
    {
        try
        {
            if (!Directory.Exists(FailRoot)) return;

            var now = DateTime.UtcNow;
            foreach (var versionDir in Directory.EnumerateDirectories(FailRoot))
            {
                var cases = Directory.EnumerateDirectories(versionDir)
                    .Select(d => new DirectoryInfo(d))
                    .OrderBy(d => CaseTimestamp(d, now))
                    .ToList();

                var removed = 0;
                foreach (var caseDir in cases)
                {
                    var age = now - CaseTimestamp(caseDir, now);
                    if (age <= Retention && cases.Count - removed <= MaxCasesPerVersion)
                        continue;

                    try
                    {
                        GeneralUpdate.Bowl.FileSystem.StorageHelper.DeleteDirectory(caseDir.FullName);
                        removed++;
                        GeneralTracer.Info($"OutboxManager: GC removed {caseDir.Name} ({versionDir}) age={age.TotalDays:0.#}d.");
                    }
                    catch (Exception ex)
                    {
                        GeneralTracer.Warn($"OutboxManager: GC could not remove {caseDir.FullName}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GeneralTracer.Warn($"OutboxManager: GC pass failed: {ex.Message}");
        }
    }

    /// <summary>Graceful-stop signal polled by the supervisor loop (written by <c>bowl stop</c>).</summary>
    public bool IsStopRequested() => File.Exists(_options.StopSignalPath);

    /// <summary>Clears the stop signal so the next watch run starts fresh.</summary>
    public void ClearStopSignal()
    {
        try
        {
            if (File.Exists(_options.StopSignalPath))
                File.Delete(_options.StopSignalPath);
        }
        catch (Exception ex)
        {
            GeneralTracer.Warn($"OutboxManager: could not clear stop signal: {ex.Message}");
        }
    }

    /// <summary>Requests a graceful stop of the watchdog watching this bowl-home.</summary>
    public static void RequestStop(string bowlHome)
    {
        var path = Path.Combine(bowlHome, "stop.signal");
        File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
    }

    /// <summary>Case age anchor: the manifest timestamp when readable, the directory timestamp otherwise.</summary>
    private static DateTime CaseTimestamp(DirectoryInfo caseDir, DateTime fallback)
    {
        try
        {
            var manifestPath = Path.Combine(caseDir.FullName, "manifest.json");
            if (!File.Exists(manifestPath))
                return caseDir.CreationTimeUtc;

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("createdAtUtc", out var created) &&
                created.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(created.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        catch
        {
            // Corrupted manifest → fall back to the directory timestamp; GC stays functional.
        }

        return fallback;
    }

    private void TryDeleteVersionRootIfEmpty()
    {
        try
        {
            if (Directory.Exists(VersionRoot) && !Directory.EnumerateFileSystemEntries(VersionRoot).Any())
                Directory.Delete(VersionRoot, false);
        }
        catch
        {
            // Cosmetic cleanup only.
        }
    }
}
