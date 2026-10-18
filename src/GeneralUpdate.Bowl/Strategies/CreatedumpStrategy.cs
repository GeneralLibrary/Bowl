using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GeneralUpdate.Bowl.Strategies;

/// <summary>
///     Tier-2 watcher built on the .NET runtime's own <c>createdump</c> tool (ships inside
///     <c>dotnet/shared/Microsoft.NETCore.App/*</c> and next to self-contained apps) — no extra
///     distribution payload, no procdump prerequisite.
/// </summary>
/// <remarks>
///     <para>
///         <b>How dumps happen with this tier:</b> <c>createdump</c> cannot attach-and-wait the
///         way procdump does; it is invoked BY the runtime at crash time when crash-dump
///         configuration is enabled (e.g. <c>DOTNET_DbgEnableMiniDump=1</c>,
///         <c>DOTNET_DbgMiniDumpName=...</c> on Linux, or the corresponding Windows registry
///         keys). This tier therefore waits for the target to exit and, after an abnormal exit,
///         collects the dump file the runtime produced — from <c>DOTNET_DbgMiniDumpName</c>
///         (supports the <c>%d</c> PID placeholder) or from the known default locations.
///     </para>
///     <para>
///         When no dump is found the session degrades to exactly the tier-3 behaviour: crash
///         report + system info without a dump. Missing dump evidence never blocks the outbox.
///     </para>
/// </remarks>
internal sealed class CreatedumpStrategy : IBowlStrategy
{
    public ProcessStartInfo? Prepare(in BowlContext context)
    {
        // No child process: the supervisor loop waits on the target directly, and the dump —
        // when the runtime produced one — is collected after the exit by CollectRuntimeDump.
        GeneralTracer.Info("CreatedumpStrategy: watching via runtime crash-dump integration (no child process).");
        return null;
    }

    /// <summary>
    ///     Presence probe for tier selection: a <c>createdump(.exe)</c> binary next to the
    ///     executable (self-contained layout) or under a well-known shared-runtime root.
    /// </summary>
    public static bool IsCreatedumpAvailable()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "createdump.exe" : "createdump";

        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName)))
            return true;

        foreach (var root in ResolveCandidateRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;

                // Shared-runtime roots carry one directory per runtime version.
                foreach (var versionDir in Directory.EnumerateDirectories(root))
                {
                    if (File.Exists(Path.Combine(versionDir, fileName)))
                        return true;
                }
            }
            catch
            {
                // Probe failures must never break watchdog startup — fall through to the next root.
            }
        }

        return false;
    }

    public Task PostProcessAsync(in BowlContext context,
        ProcessExitResult exitResult, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Best-effort collection of the runtime-produced dump after an abnormal exit.
    ///     Returns the collected dump path, or <c>null</c> when the runtime did not write one.
    /// </summary>
    /// <param name="targetPid">PID of the crashed process (resolves the %d placeholder).</param>
    /// <param name="failDirectory">Evidence directory the dump is moved into.</param>
    /// <param name="dumpFileName">Canonical dump file name inside the evidence directory.</param>
    public string? CollectRuntimeDump(int targetPid, string failDirectory, string dumpFileName)
    {
        foreach (var candidate in EnumerateDumpCandidates(targetPid))
        {
            try
            {
                if (!File.Exists(candidate)) continue;

                Directory.CreateDirectory(failDirectory);
                var destination = Path.Combine(failDirectory, dumpFileName);
                // Same volume (temp/evidence roots aside) — Copy+Delete survives cross-device
                // layouts where Move would throw; evidence must never be lost to a move error.
                File.Copy(candidate, destination, overwrite: true);
                File.Delete(candidate);
                GeneralTracer.Info($"CreatedumpStrategy: collected runtime dump {candidate} -> {destination}.");
                return destination;
            }
            catch (Exception ex)
            {
                GeneralTracer.Warn($"CreatedumpStrategy: failed to collect dump {candidate}: {ex.Message}");
            }
        }

        GeneralTracer.Info("CreatedumpStrategy: runtime produced no dump — degrading to crash-report-only evidence.");
        return null;
    }

    /// <summary>
    ///     Dump locations probed in priority order: the environment override (with the %d PID
    ///     placeholder), the self-contained layout (next to the watchdog executable), then the
    ///     well-known shared-runtime directories.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> EnumerateDumpCandidates(int targetPid)
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Replace("%d", targetPid.ToString());
        }

        foreach (var root in ResolveCandidateRoots())
        {
            string[] patterns =
            {
                $"coredump.{targetPid}",
                $"core.{targetPid}",
                "createdump.dmp",
                "coredump.dmp"
            };
            foreach (var pattern in patterns)
                yield return Path.Combine(root, pattern);
        }
    }

    private static System.Collections.Generic.IReadOnlyList<string> ResolveCandidateRoots()
    {
        var roots = new System.Collections.Generic.List<string>
        {
            // Self-contained publish: createdump writes next to the app host.
            AppDomain.CurrentDomain.BaseDirectory,
        };

        try
        {
            var processModule = Process.GetCurrentProcess().MainModule?.FileName;
            var exeDir = string.IsNullOrEmpty(processModule)
                ? null
                : Path.GetDirectoryName(processModule);
            if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
                roots.Add(exeDir);
        }
        catch
        {
            // MainModule can be unavailable on some Unix configurations — skip silently.
        }

        // Shared runtime layouts (Windows + typical Unix install roots).
        roots.Add(Path.Combine("/usr", "share", "dotnet", "shared", "Microsoft.NETCore.App"));
        roots.Add(Path.Combine("/usr", "lib", "dotnet", "shared", "Microsoft.NETCore.App"));
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
            roots.Add(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App"));

        return roots;
    }
}
