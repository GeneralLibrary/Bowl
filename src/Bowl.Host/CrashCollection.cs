using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GeneralUpdate.Bowl;
using GeneralUpdate.Bowl.Internal;
using GeneralUpdate.Bowl.Strategies;

namespace Bowl.Host;

// Diagnostic capture is independent of the v1 outcome and recovery journal.
internal sealed class CrashCollection(string root, Request request)
{
    private readonly List<(CancellationTokenSource Cancel, Task Work)> sessions = [];
    private readonly HashSet<ProcessIdentity> started = [];
    private readonly ConcurrentDictionary<ProcessIdentity, string> states = new();

    internal static bool SafeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128 ||
            version is "." or ".." || version.EndsWith(' ') || version.EndsWith('.') ||
            version.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            return false;
        var stem = version.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return false;
        return !(stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'));
    }

    public void Start(ProcessIdentity identity)
    {
        if (!request.CrashCollectionEnabled || !started.Add(identity))
            return;
        states[identity] = "capturing";
        var cancel = new CancellationTokenSource();
        sessions.Add((cancel, Task.Run(() => CaptureAsync(identity, cancel.Token))));
    }

    public CaptureStatus Status() => new()
    {
        AttemptId = request.AttemptId,
        Targets = states.Select(entry => new CaptureTargetStatus(entry.Key, entry.Value)).ToArray()
    };

    public async Task StopAsync(bool failure)
    {
        if (failure && sessions.Count != 0)
            await Task.WhenAny(Task.WhenAll(sessions.Select(s => s.Work)), Task.Delay(TimeSpan.FromSeconds(3)));
        foreach (var session in sessions)
            session.Cancel.Cancel();
        var all = Task.WhenAll(sessions.Select(s => s.Work));
        if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))) != all)
            Console.Error.WriteLine("Crash collection did not stop in time; partial evidence requires inspection.");
        else
        {
            await all;
            foreach (var session in sessions)
                session.Cancel.Dispose();
        }
    }

    private async Task CaptureAsync(ProcessIdentity identity, CancellationToken token)
    {
        string? staging = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Host crash capture requires Windows procdump.");
            if (identity.Pid == Environment.ProcessId)
                throw new InvalidDataException("Host cannot capture itself.");
            using var target = Process.GetProcessById(identity.Pid);
            _ = target.Handle;
            if (target.HasExited || !Processes.SameProcessStart(target.StartTime.ToUniversalTime(), identity.StartTimeUtc))
                throw new InvalidDataException("Capture target identity is unavailable.");
            var targetName = Path.GetFileName(target.MainModule?.FileName);
            if (string.IsNullOrWhiteSpace(targetName))
                throw new InvalidDataException("Capture target name is unavailable.");

            var versionRoot = Path.Combine(root, "crash-outbox", request.TargetVersion!);
            Paths.NoLinks(versionRoot);
            Directory.CreateDirectory(versionRoot);
            var guid = Guid.NewGuid().ToString("D");
            staging = Path.Combine(versionRoot, "." + request.AttemptId + "-" + guid + ".partial");
            Paths.NoLinks(staging);
            Directory.CreateDirectory(staging);
            var context = new BowlContext
            {
                ProcessNameOrId = identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DumpFileName = "crash.dmp",
                FailFileName = "crash.json",
                TargetPath = request.InstallPath,
                FailDirectory = staging,
                BackupDirectory = string.Empty,
                WorkModel = "Normal",
                ExtendedField = request.TargetVersion!,
                DumpType = DumpType.Mini,
                AutoRestore = false
            };
            GeneralTracer.SetTracingEnabled(false);
            var info = new WindowsBowlStrategy().Prepare(context)
                ?? throw new InvalidOperationException("Capture tool unavailable.");
            if (!Paths.Contains(Paths.Absolute(AppContext.BaseDirectory), Path.GetFullPath(info.FileName)))
                throw new InvalidDataException("Capture tool must be deployed with the host.");
            Paths.NoLinks(info.FileName);
            if (!File.Exists(info.FileName))
                throw new FileNotFoundException("Capture tool unavailable.");
            token.ThrowIfCancellationRequested();
            if (target.HasExited || !Processes.SameProcessStart(target.StartTime.ToUniversalTime(), identity.StartTimeUtc))
                throw new InvalidDataException("Capture target exited before attach.");
            using var job = CaptureJob.Create();
            using var tool = Process.Start(info) ?? throw new InvalidOperationException("Capture tool failed to start.");
            try { job.Assign(tool); }
            catch (Win32Exception)
            {
                if (!tool.HasExited)
                    tool.Kill();
                throw;
            }
            var stdout = tool.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = tool.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var exit = tool.WaitForExitAsync();
            var canceled = Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (await Task.WhenAny(exit, canceled) == canceled && !tool.HasExited)
                await StopToolAsync(tool);
            await exit;
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            var dump = Path.Combine(staging, context.DumpFileName);
            Paths.NoLinks(dump);
            if (tool.ExitCode == 0 && !File.Exists(dump))
            {
                states[identity] = "noDump";
                Console.Error.WriteLine("Crash capture finished without a dump; the cause is unknown.");
                return;
            }
            if (!DumpSucceeded(tool.ExitCode, dump))
            {
                Console.Error.WriteLine($"Crash capture produced no complete dump (tool exit {tool.ExitCode}).");
                throw new InvalidOperationException("Crash capture did not produce a complete dump.");
            }

            await new CrashReporter().GenerateReportAsync(context, Array.Empty<string>(), token);
            Publish(staging, guid, targetName, request.TargetVersion!);
            staging = null;
            states[identity] = "published";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            states[identity] = "interrupted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or Win32Exception or ArgumentException or NotSupportedException or
            System.Security.SecurityException or JsonException or TypeInitializationException)
        {
            states[identity] = "unavailable";
            Console.Error.WriteLine($"Crash collection unavailable: {ex.GetType().Name}.");
        }
        finally
        {
            if (staging != null && Directory.Exists(staging))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(staging).Any())
                        Directory.Delete(staging);
                    else
                        Console.Error.WriteLine("Incomplete crash evidence retained for operator inspection.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine($"Crash evidence cleanup unavailable: {ex.GetType().Name}."); }
            }
        }
    }

    private static async Task StopToolAsync(Process tool)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Host executable is unavailable.");
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--stop-capture");
        info.ArgumentList.Add(tool.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("Capture stop helper failed to start.");
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0 && !tool.HasExited)
            throw new InvalidOperationException("Capture stop helper failed.");
    }

    // ProcDump returns the number of captured dumps, not zero on success.
    internal static bool DumpSucceeded(int exitCode, string dump) =>
        exitCode == 1 && File.Exists(dump) && new FileInfo(dump).Length != 0;

    internal static void Publish(string staging, string guid, string targetName, string version)
    {
        if (!Guid.TryParseExact(guid, "D", out var id) || id.ToString("D") != guid ||
            !SafeVersion(version) || string.IsNullOrWhiteSpace(targetName) ||
            Path.GetFileName(Path.GetDirectoryName(staging)) != version)
            throw new InvalidDataException("Invalid crash case identity.");
        Paths.NoLinks(staging);
        foreach (var file in new[] { Path.Combine(staging, "crash.dmp"), Path.Combine(staging, "crash.json") })
        {
            Paths.NoLinks(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length == 0)
                throw new InvalidDataException("Crash evidence is empty.");
            stream.Flush(flushToDisk: true);
        }
        WriteManifest(staging, guid, targetName, version);
        Directory.Move(staging, Path.Combine(Path.GetDirectoryName(staging)!, guid));
    }

    private static void WriteManifest(string staging, string guid, string targetName, string version)
    {
        var path = Path.Combine(staging, "manifest.json");
        Paths.NoLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema", 2);
        writer.WriteString("guid", guid);
        writer.WriteString("kind", "crash");
        writer.WriteString("targetName", targetName);
        writer.WriteString("version", version);
        writer.WriteNull("exitCode");
        writer.WriteString("createdAtUtc", DateTimeOffset.UtcNow);
        writer.WriteStartArray("files");
        writer.WriteStringValue("crash.json");
        writer.WriteStringValue("crash.dmp");
        writer.WriteEndArray();
        writer.WriteString("state", "pending");
        writer.WriteNumber("attempts", 0);
        writer.WriteEndObject();
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}
