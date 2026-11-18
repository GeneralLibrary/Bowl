using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GeneralUpdate.Bowl.FileSystem;
using GeneralUpdate.Bowl;

namespace GeneralUpdate.Bowl.Strategies;

/// <summary>
/// Windows crash surveillance strategy using procdump.
/// </summary>
internal sealed class WindowsBowlStrategy : IBowlStrategy
{
    public ProcessStartInfo? Prepare(in BowlContext context)
    {
        GeneralTracer.Info("WindowsBowlStrategy.Prepare: resolving procdump binary.");

        var procdumpExe = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "procdump.exe",
            Architecture.X64 => "procdump64.exe",
            _ => "procdump64a.exe",
        };

        // Tier selection (StrategyFactory) probes for procdump under the watchdog's own
        // BaseDirectory, because the console watchdog is deployed independently of the
        // monitored application. Resolve the binary from the same location first and fall
        // back to the target install path so in-process (API-embedded) consumers keep
        // working; resolving only from TargetPath silently broke the standalone
        // deployment form (procdump launch failed on every session).
        var procdumpCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Applications", "Windows", procdumpExe),
            Path.Combine(context.TargetPath, "Applications", "Windows", procdumpExe),
        };
        var appPath = procdumpCandidates.FirstOrDefault(File.Exists)
            ?? procdumpCandidates[0];
        var dumpFullPath = Path.Combine(context.FailDirectory, context.DumpFileName);

        // Map DumpType to procdump flag
        var dumpFlag = context.DumpType switch
        {
            DumpType.Mini => "-mm",
            DumpType.Heap => "-mh",
            _ => "-ma", // Full
        };

        EnsureDirectory(context.FailDirectory);

        var arguments = $"-e {dumpFlag} {context.ProcessNameOrId} \"{dumpFullPath}\"";
        GeneralTracer.Info($"WindowsBowlStrategy.Prepare: app={appPath}, args={arguments}");

        return new ProcessStartInfo
        {
            FileName = appPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    public Task PostProcessAsync(in BowlContext context,
        ProcessExitResult exitResult, CancellationToken ct)
    {
        // No additional Windows-specific post-processing.
        // Restore, environment variable, and crash report are handled by Bowl itself.
        return Task.CompletedTask;
    }

    private static void EnsureDirectory(string path)
    {
        // Create the fail directory if it does not yet exist.
        // Do NOT delete an existing directory — that would destroy
        // crash diagnostics from previous surveillance sessions.
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
