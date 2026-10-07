using System.Diagnostics;

namespace Bowl.Host;

internal interface IProcesses
{
    bool IsAlive(ProcessIdentity identity);
    Task StopAsync(ProcessIdentity identity, CancellationToken token);
}

internal sealed class Processes : IProcesses
{
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
            if (process.HasExited || process.StartTime.ToUniversalTime() != identity.StartTimeUtc.UtcDateTime)
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
