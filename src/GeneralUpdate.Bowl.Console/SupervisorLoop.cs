using System.Diagnostics;
using System.Runtime.InteropServices;
using GeneralUpdate.Bowl;
using GeneralUpdate.Bowl.Internal;
using GeneralUpdate.Bowl.Strategies;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     The supervision loop (§2.3) — turns the library's single-shot surveillance into a
///     life-cycle guardian: find target → attach watcher → wait exit → crash verdict →
///     evidence outbox → re-arm. A same-named new process appearing afterwards is treated as
///     the user restarting the application and re-arms automatically; a target that never
///     appears for <c>idle-exit</c> ends the watchdog (no orphaned resident burning resources).
/// </summary>
internal sealed class SupervisorLoop
{
    private static readonly TimeSpan TargetPollInterval = TimeSpan.FromSeconds(5);

    private readonly WatchOptions _options;
    private readonly OutboxManager _outbox;
    private readonly IProcessWatcher _watcher;
    private readonly string? _onCrashCommand;

    public SupervisorLoop(WatchOptions options, OutboxManager outbox, IProcessWatcher watcher)
    {
        _options = options;
        _outbox = outbox;
        _watcher = watcher;
        _onCrashCommand = options.OnCrash;
    }

    /// <summary>Runs until idle-exit, stop signal, or cancellation. Returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        GeneralTracer.Info(
            $"SupervisorLoop: start target='{_options.TargetName}' install='{_options.InstallPath}' " +
            $"version='{_options.Version}' watcher='{_watcher.Name}' idleExit={_options.IdleExit}.");

        _outbox.ClearStopSignal();
        _outbox.CollectGarbage();

        var lastSeen = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_outbox.IsStopRequested())
            {
                GeneralTracer.Info("SupervisorLoop: stop signal received — exiting gracefully (code 0).");
                _outbox.ClearStopSignal();
                return ExitCodes.Success;
            }

            var target = FindTarget();
            if (target == null)
            {
                if (DateTime.UtcNow - lastSeen > _options.IdleExit)
                {
                    GeneralTracer.Info(
                        $"SupervisorLoop: target absent for more than idle-exit ({_options.IdleExit}) — exiting (code 0).");
                    return ExitCodes.Success;
                }

                await Task.Delay(TargetPollInterval, ct);
                continue;
            }

            // Target found — the idle window is irrelevant until it disappears again.
            lastSeen = DateTime.UtcNow;

            var session = await SuperviseSessionAsync(target, ct);
            if (session.Crash)
            {
                await CollectEvidenceAsync(session, ct);
                RunOnCrashHook(session.Directory);
                _outbox.CollectGarbage(); // crash produced a case — enforce retention now
            }
            else
            {
                // Normal exit produces no evidence (§2.3); the pre-created session directory
                // must not linger and masquerade as a pending case.
                _outbox.RemoveSessionIfEmpty(session.Directory);
                GeneralTracer.Info(
                    $"SupervisorLoop: target exited normally (code {session.Outcome.TargetExitCode}) — no evidence, re-arming.");
            }

            // Re-arm: the loop continues and re-attaches when the user restarts the app.
            lastSeen = DateTime.UtcNow;
        }
    }

    /// <summary>First live process matching the watched name, or <c>null</c>.</summary>
    private Process? FindTarget()
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(_options.TargetName);
        }
        catch (Exception ex)
        {
            GeneralTracer.Warn($"SupervisorLoop: process lookup failed: {ex.Message}");
            return null;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                // Touch the handle: an exited-between-enumeration process throws here and
                // must not be selected as the watch target.
                _ = candidate.Id;
                return candidate;
            }
            catch
            {
                candidate.Dispose();
            }
        }

        foreach (var candidate in candidates)
            candidate.Dispose();
        return null;
    }

    // ── One session ─────────────────────────────────────────────────────

    private sealed record SessionResult(string Directory, bool Crash, WatchOutcome Outcome);

    private async Task<SessionResult> SuperviseSessionAsync(Process target, CancellationToken ct)
    {
        var sessionDirectory = _outbox.CreateSessionDirectory();
        var context = BuildContext(sessionDirectory, target.Id);
        GeneralTracer.Info(
            $"SupervisorLoop: session started (PID {target.Id}) — evidence directory {sessionDirectory}.");

        try
        {
            var outcome = await _watcher.WaitAsync(target, context, ct);
            var crash = outcome.DumpPath != null || ExitCodeWatcherStrategy.IsCrashExitCode(outcome.TargetExitCode);
            return new SessionResult(sessionDirectory, crash, outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A session that cannot run must not kill the watchdog: re-arm and let the next
            // session retry. The exit code is unknown, so this is deliberately NOT a verdict.
            GeneralTracer.Error("SupervisorLoop: watch session failed — re-arming.", ex);
            return new SessionResult(sessionDirectory, false, WatchOutcome.Empty);
        }
    }

    /// <summary>
    ///     Evidence pipeline for a crashed session: crash report + system info + dump (already
    ///     in the session directory by construction) + manifest. Every artifact is best-effort —
    ///     a failing piece degrades evidence quality but the manifest is always written so the
    ///     case stays deliverable.
    /// </summary>
    private async Task CollectEvidenceAsync(SessionResult session, CancellationToken ct)
    {
        GeneralTracer.Warn(
            $"SupervisorLoop: CRASH detected (exitCode={session.Outcome.TargetExitCode}, " +
            $"dump={(session.Outcome.DumpPath != null ? "captured" : "unavailable")}) — collecting evidence.");

        var context = BuildContext(session.Directory, pid: 0);

        // 1. Crash report JSON ({version}_fail.json) — the library's reporter.
        try
        {
            await new CrashReporter().GenerateReportAsync(context, session.Outcome.OutputLines, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            GeneralTracer.Error("SupervisorLoop: crash report generation failed.", ex);
        }

        // 2. System information (sysinfo) — platform provider, non-fatal on failure.
        try
        {
            await SystemInfoProviderFactory.Create().ExportAsync(session.Directory, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            GeneralTracer.Error("SupervisorLoop: system info export failed.", ex);
        }

        // 3. Manifest LAST: directory becomes visible as pending only once complete.
        _outbox.WriteManifest(session.Directory, session.Outcome);
    }

    /// <summary>Library context for one session. WorkModel is always "Normal": Bowl collects
    /// evidence and decides nothing — restore/rollback decisions belong to Core.</summary>
    private BowlContext BuildContext(string sessionDirectory, int pid) => new BowlContext
    {
        // Watch the exact PID: procdump and reports must never latch onto an unrelated
        // process that happens to reuse the name between re-arm cycles.
        ProcessNameOrId = pid > 0 ? pid.ToString() : _options.TargetName,
        DumpFileName = _options.DumpFileName,
        FailFileName = _options.FailFileName,
        TargetPath = _options.InstallPath,
        FailDirectory = sessionDirectory,
        BackupDirectory = Path.Combine(_options.InstallPath, _options.Version), // legacy field, unused by Normal model
        WorkModel = "Normal",
        ExtendedField = _options.Version,
        TimeoutMs = int.MaxValue, // sessions are supervised here, not time-boxed
        DumpType = MapDumpType(_options.DumpType),
    }.Normalize();

    private static DumpType MapDumpType(string value) => value.ToLowerInvariant() switch
    {
        "mini" => DumpType.Mini,
        "heap" => DumpType.Heap,
        _ => DumpType.Full,
    };

    // ── Advanced hook ───────────────────────────────────────────────────

    /// <summary>
    ///     Runs the optional <c>--on-crash</c> external command with the evidence directory
    ///     appended as an argument (replaces the removed AutoRestore: users wire their own
    ///     restore script and own the risk). Failures are logged, never propagated — the
    ///     evidence is already safely on disk.
    /// </summary>
    private void RunOnCrashHook(string evidenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(_onCrashCommand)) return;

        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var startInfo = isWindows
                ? new ProcessStartInfo("cmd.exe", $"/c {_onCrashCommand} \"{evidenceDirectory}\"")
                : new ProcessStartInfo("/bin/sh", $"-c {_onCrashCommand} \"{evidenceDirectory}\"");
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            GeneralTracer.Info($"SupervisorLoop: on-crash hook starting: {_onCrashCommand}");
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                GeneralTracer.Warn("SupervisorLoop: on-crash hook could not be started.");
                return;
            }

            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                GeneralTracer.Warn("SupervisorLoop: on-crash hook timed out after 60s and was killed.");
            }
            else
            {
                GeneralTracer.Info($"SupervisorLoop: on-crash hook finished (code {process.ExitCode}).");
            }
        }
        catch (Exception ex)
        {
            GeneralTracer.Error("SupervisorLoop: on-crash hook failed.", ex);
        }
    }
}
