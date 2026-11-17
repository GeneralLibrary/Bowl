using System.Diagnostics;

namespace Bowl.Host;

internal interface IProcesses
{
    bool IsAlive(ProcessIdentity identity);
    Task StopAsync(ProcessIdentity identity, CancellationToken token);
}

internal sealed class Processes : IProcesses
{
    // On Linux, Process.StartTime is recomputed on every read as boot-time plus
    // /proc ticks, and the boot-time estimate (second-resolution uptime) carries
    // up to ~1s of jitter between reads — an exact comparison across two reads
    // false-negatives even for the same live process. Windows kernel timestamps
    // are exact, so equality stays strict there.
    private static readonly bool ExactTimestamps = OperatingSystem.IsWindows();
    private static readonly TimeSpan StartJitter = TimeSpan.FromSeconds(2);

    public bool IsAlive(ProcessIdentity identity)
    {
        using var process = Find(identity);
        return process != null;
    }

    private static Process? Find(ProcessIdentity identity)
    {
        if (identity.Pid <= 0 || identity.StartTimeUtc == default)
            throw new InvalidDataException("Invalid process identity.");
        Process process;
        try { process = Process.GetProcessById(identity.Pid); }
        catch (ArgumentException) { return null; } // The PID no longer exists.
        try
        {
            if (process.HasExited || !SameProcessStart(process.StartTime.ToUniversalTime(), identity.StartTimeUtc))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch
        {
            process.Dispose();
            throw; // Access denied is NOT evidence of process exit.
        }
    }

    internal static bool SameProcessStart(DateTimeOffset observed, DateTimeOffset recorded)
    {
        var difference = observed - recorded;
        return ExactTimestamps
            ? difference == TimeSpan.Zero
            : Math.Abs(difference.TotalSeconds) <= StartJitter.TotalSeconds;
    }

    public async Task StopAsync(ProcessIdentity identity, CancellationToken token)
    {
        using var process = Find(identity);
        if (process == null)
            return;
        // Never kill by process name, reused PID or an unrelated process tree.
        process.Kill();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
    }
}
