using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GeneralUpdate.Bowl.Strategies;

/// <summary>
///     Tier-3 all-platform fallback watcher: no external tool, no dump. The supervisor loop's
///     exit-code verdict (<see cref="IsCrashExitCode"/>) decides crash vs. normal exit; the
///     crash report and system information are still produced so the evidence outbox always
///     carries at least text diagnostics.
/// </summary>
/// <remarks>
///     <see cref="Prepare"/> returns <c>null</c> by design — the caller (supervisor loop) then
///     waits on the target process directly instead of a procdump child. This is the same
///     contract <see cref="BowlBootstrap"/> already treats as "tool unavailable", which keeps
///     the embedded single-shot API unchanged.
/// </remarks>
internal sealed class ExitCodeWatcherStrategy : IBowlStrategy
{
    /// <summary>
    ///     The exit-code crash verdict shared by every watcher tier: a non-zero exit code is a
    ///     crash ("normal exit produces no evidence"). A dump, when a higher tier captured one,
    ///     is additional evidence — never a substitute for this rule.
    /// </summary>
    public static bool IsCrashExitCode(int exitCode) => exitCode != 0;

    public ProcessStartInfo? Prepare(in BowlContext context)
    {
        GeneralTracer.Info("ExitCodeWatcherStrategy: no dump-capable tool available — watching by exit code only.");
        return null;
    }

    public Task PostProcessAsync(in BowlContext context,
        ProcessExitResult exitResult, CancellationToken ct)
    {
        // Nothing platform-specific to do; report + system info are handled by the caller.
        return Task.CompletedTask;
    }
}
