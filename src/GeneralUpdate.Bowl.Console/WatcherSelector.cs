using System.Diagnostics;
using System.Runtime.InteropServices;
using GeneralUpdate.Bowl;
using GeneralUpdate.Bowl.Strategies;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>Result of one watch session: the target has exited when the outcome is returned.</summary>
/// <param name="TargetExitCode">Exit code of the watched process, or <c>null</c> when unavailable.</param>
/// <param name="DumpPath">Dump captured for this session, or <c>null</c>.</param>
/// <param name="OutputLines">Tool output lines (procdump stdout/stderr), for the crash report.</param>
internal sealed record WatchOutcome(
    int? TargetExitCode = null,
    string? DumpPath = null,
    IReadOnlyList<string>? ToolOutput = null)
{
    public static readonly WatchOutcome Empty = new();
    public IReadOnlyList<string> OutputLines => ToolOutput ?? Array.Empty<string>();
}

/// <summary>
///     One watch session. Implementations return only after the target process has exited —
///     the supervisor loop then renders the crash verdict (dump present OR non-zero exit code)
///     and collects the evidence outbox.
/// </summary>
internal interface IProcessWatcher
{
    /// <summary>Watcher name for logs (<c>procdump</c> / <c>createdump</c> / <c>exit-code</c>).</summary>
    string Name { get; }

    /// <summary>Waits for the target to exit, capturing whatever evidence the tier can produce.</summary>
    Task<WatchOutcome> WaitAsync(Process target, BowlContext context, CancellationToken ct);
}

/// <summary>
///     Watcher layer: selects the highest-capable tier once at startup (§4.4) and wraps the
///     library strategies into <see cref="IProcessWatcher"/> sessions. Selection never fails —
///     the exit-code tier always exists, so the console's exit code 4 is a reserved case.
/// </summary>
internal static class WatcherSelector
{
    public static IProcessWatcher Select(out WatcherTier tier)
    {
        var strategy = StrategyFactory.CreateWithFallback(out tier);
        GeneralTracer.Info($"WatcherSelector: tier={tier}.");

        return tier switch
        {
            WatcherTier.Procdump => new ProcdumpWatcher(strategy),
            WatcherTier.Createdump => new CreatedumpWatcher((CreatedumpStrategy)strategy),
            _ => new ExitCodeWatcher(),
        };
    }
}

/// <summary>Tier 1: run the procdump child for the whole session; dump presence is the crash evidence.</summary>
internal sealed class ProcdumpWatcher : IProcessWatcher
{
    private readonly IBowlStrategy _strategy;

    public ProcdumpWatcher(IBowlStrategy strategy) => _strategy = strategy;

    public string Name => "procdump";

    public async Task<WatchOutcome> WaitAsync(Process target, BowlContext context, CancellationToken ct)
    {
        var startInfo = _strategy.Prepare(context);
        if (startInfo == null)
        {
            // Session-level degradation (e.g. the Linux procdump install failed mid-flight):
            // fall back to the exit-code verdict instead of losing the session entirely.
            GeneralTracer.Warn("ProcdumpWatcher: tool unavailable this session — falling back to exit-code verdict.");
            return await ExitCodeWatcher.WaitTargetAsync(target, ct);
        }

        var outputLines = await RunToolAsync(startInfo, ct);

        // The procdump child exits because the target exits; settle the final race so the
        // exit code is reliably readable before we render any verdict.
        await target.WaitForExitAsync(ct);

        var dumpPath = Path.Combine(context.FailDirectory, context.DumpFileName);
        return new WatchOutcome
        {
            TargetExitCode = ExitCodeWatcher.TryGetExitCode(target),
            DumpPath = File.Exists(dumpPath) ? dumpPath : null,
            ToolOutput = outputLines,
        };
    }

    /// <summary>
    ///     Console-side tool runner: waits WITHOUT a timeout (a watchdog session legitimately
    ///     runs for days — the library's fixed-timeout <c>ProcessRunner</c> would kill a healthy
    ///     procdump), while remaining cancellable for shutdown.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RunToolAsync(
        ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        using var cancellation = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { /* process already gone */ }
        });

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            GeneralTracer.Error($"ProcdumpWatcher: failed to start tool '{startInfo.FileName}': {ex.Message}");
            return Array.Empty<string>();
        }

        GeneralTracer.Info($"ProcdumpWatcher: tool started (PID {process.Id}), waiting for the target to exit.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var lines = new List<string>();
        lines.AddRange(SplitLines(await stdoutTask));
        lines.AddRange(SplitLines(await stderrTask));
        GeneralTracer.Info($"ProcdumpWatcher: tool exited (code {process.ExitCode}), {lines.Count} output line(s).");
        return lines;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
///     Tier 2: wait for the target directly; after an abnormal exit, collect the dump the
///     .NET runtime's createdump integration produced (when crash-dump configuration is on).
/// </summary>
internal sealed class CreatedumpWatcher : IProcessWatcher
{
    private readonly CreatedumpStrategy _strategy;

    public CreatedumpWatcher(CreatedumpStrategy strategy) => _strategy = strategy;

    public string Name => "createdump";

    public async Task<WatchOutcome> WaitAsync(Process target, BowlContext context, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var exitCode = await ExitCodeWatcher.WaitTargetAsync(target, ct);
        if (exitCode.TargetExitCode is null || ExitCodeWatcherStrategy.IsCrashExitCode(exitCode.TargetExitCode.Value))
        {
            var dump = _strategy.CollectRuntimeDump(
                target.Id, context.FailDirectory, context.DumpFileName,
                exitCode.TargetExitCode is null ? started : null, requirePid: exitCode.TargetExitCode is null);
            return exitCode with { DumpPath = dump };
        }

        return exitCode;
    }
}

/// <summary>Tier 3: wait for the target; the exit code alone decides crash vs. normal exit.</summary>
internal sealed class ExitCodeWatcher : IProcessWatcher
{
    public string Name => "exit-code";

    public Task<WatchOutcome> WaitAsync(Process target, BowlContext context, CancellationToken ct)
        => WaitTargetAsync(target, ct);

    /// <summary>Shared wait: returns the exit code once the target has fully exited.</summary>
    internal static async Task<WatchOutcome> WaitTargetAsync(Process target, CancellationToken ct)
    {
        await target.WaitForExitAsync(ct);
        var code = TryGetExitCode(target);
        GeneralTracer.Info($"ExitCodeWatcher: target exited with code {(code?.ToString() ?? "unavailable")}.");
        return new WatchOutcome { TargetExitCode = code };
    }

    internal static int? TryGetExitCode(Process target)
    {
        try { return target.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }
}
