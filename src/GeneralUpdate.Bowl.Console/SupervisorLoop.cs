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

    /// <summary>How often an active watch session checks for a pending stop signal.</summary>
    private static readonly TimeSpan StopSignalPollInterval = TimeSpan.FromSeconds(5);

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

        var stopGeneration = _outbox.ReadStopGeneration();
        _outbox.CollectGarbage();

        // The main loop only checked the stop signal BETWEEN sessions, but a healthy watch
        // session can run for days — `bowl stop` returned 0 while the watchdog kept running.
        // Link the caller's token with a periodic stop-signal poll so an active session is
        // interrupted too (every watcher layer consumes the token via WaitForExitAsync).
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopPoller = new Timer(_ =>
        {
            try
            {
                if (_outbox.IsStopRequested(stopGeneration))
                    stopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Timer callback raced loop disposal — nothing to do.
            }
            catch (IOException ex)
            {
                GeneralTracer.Warn($"SupervisorLoop: could not read stop signal: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                GeneralTracer.Warn($"SupervisorLoop: could not read stop signal: {ex.Message}");
            }
        });
        stopPoller.Change(TimeSpan.Zero, StopSignalPollInterval);

        try
        {
            var lastSeen = DateTime.UtcNow;
            while (true)
            {
                stopCts.Token.ThrowIfCancellationRequested();

                if (_outbox.IsStopRequested(stopGeneration))
                {
                    GeneralTracer.Info("SupervisorLoop: stop signal received — exiting gracefully (code 0).");
                    return ExitCodes.Success;
                }

                using var target = FindTarget();
                if (target == null)
                {
                    if (DateTime.UtcNow - lastSeen > _options.IdleExit)
                    {
                        GeneralTracer.Info(
                            $"SupervisorLoop: target absent for more than idle-exit ({_options.IdleExit}) — exiting (code 0).");
                        return ExitCodes.Success;
                    }

                    await Task.Delay(TargetPollInterval, stopCts.Token);
                    continue;
                }

                // Target found — the idle window is irrelevant until it disappears again.
                lastSeen = DateTime.UtcNow;

                OutboxSession? lease;
                try
                {
                    lease = _outbox.CreateSession();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    GeneralTracer.Error("SupervisorLoop: cannot create evidence session; continuing with exit-code monitoring.", ex);
                    lease = null;
                }

                using (lease)
                {
                    var session = await SuperviseSessionAsync(target, lease?.Directory, stopCts.Token);
                    if (session.Crash && lease != null)
                    {
                        await CollectEvidenceAsync(session, stopCts.Token);
                        PublishEvidence(session.Directory!, session.Outcome, _onCrashCommand, AppContext.BaseDirectory);
                        _outbox.CollectGarbage();
                    }
                    else
                    {
                        if (lease != null) _outbox.RemoveSessionIfEmpty(lease.Directory);
                        if (session.Crash)
                            GeneralTracer.Warn("SupervisorLoop: crash detected without an evidence session; evidence was not captured.");
                        else
                            GeneralTracer.Info(session.Outcome.TargetExitCode is null
                                ? "SupervisorLoop: target exit status unavailable — no crash evidence, re-arming."
                                : $"SupervisorLoop: target exited normally (code {session.Outcome.TargetExitCode}) — no evidence, re-arming.");
                    }
                }

                // Re-arm: the loop continues and re-attaches when the user restarts the app.
                lastSeen = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancellation came from the stop-signal poller, not the host process: unwind
            // the interrupted session gracefully and honour the stop request.
            GeneralTracer.Info("SupervisorLoop: stop signal interrupted the active session — exiting gracefully (code 0).");
            return ExitCodes.Success;
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
                // Release the handles of the not-selected siblings before returning — a
                // long-lived watchdog leaks one process handle per session otherwise.
                foreach (var other in candidates)
                {
                    if (!ReferenceEquals(other, candidate))
                        other.Dispose();
                }
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

    internal sealed record SessionResult(string? Directory, bool Crash, WatchOutcome Outcome);

    internal async Task<SessionResult> SuperviseSessionAsync(Process target, string? sessionDirectory, CancellationToken ct)
    {
        GeneralTracer.Info(
            $"SupervisorLoop: session started (PID {target.Id}) — evidence directory {sessionDirectory ?? "(unavailable)"}.");

        try
        {
            var outcome = sessionDirectory == null
                ? await ExitCodeWatcher.WaitTargetAsync(target, ct)
                : await _watcher.WaitAsync(target, BuildContext(sessionDirectory, target.Id), ct);
            var crash = outcome.DumpPath != null ||
                (outcome.TargetExitCode is int code && ExitCodeWatcherStrategy.IsCrashExitCode(code));
            if (!crash && outcome.TargetExitCode is null)
                GeneralTracer.Warn("SupervisorLoop: target exit status unavailable and no dump found; crash verdict unknown.");
            return new SessionResult(sessionDirectory, crash, outcome);
        }

        catch (OperationCanceledException)
        {
            // The session was interrupted (stop signal / shutdown): the pre-created
            // session directory holds no evidence and must not linger as a look-alike case.
            if (sessionDirectory != null) _outbox.RemoveSessionIfEmpty(sessionDirectory);
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

    internal bool PublishEvidence(string directory, WatchOutcome outcome, string? command, string scriptDirectory)
    {
        if (!RunOnCrashHook(directory, command, scriptDirectory))
        {
            GeneralTracer.Warn($"SupervisorLoop: hook termination not confirmed; case remains unpublished: {directory}.");
            return false;
        }
        try
        {
            _outbox.WriteManifest(directory, outcome);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GeneralTracer.Error($"SupervisorLoop: manifest publication failed; case remains unpublished: {directory}.", ex);
            return false;
        }
    }

    /// <summary>
    ///     Evidence pipeline for a crashed session: crash report + system info + dump (already
    ///     in the session directory by construction). Publishing follows the hook.
    /// </summary>
    private async Task CollectEvidenceAsync(SessionResult session, CancellationToken ct)
    {
        GeneralTracer.Warn(
            $"SupervisorLoop: CRASH detected (exitCode={session.Outcome.TargetExitCode}, " +
            $"dump={(session.Outcome.DumpPath != null ? "captured" : "unavailable")}) — collecting evidence.");

        var context = BuildContext(session.Directory!, pid: 0);

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
            await SystemInfoProviderFactory.Create().ExportAsync(session.Directory!, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            GeneralTracer.Error("SupervisorLoop: system info export failed.", ex);
        }
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
    ///     Activates the optional <c>--on-crash</c> hook. The executed script is the
    ///     fixed-name <c>on-crash.cmd</c> / <c>on-crash.sh</c> deployed next to the
    ///     watchdog; the evidence directory travels via the child-only
    ///     <c>BOWL_EVIDENCE_DIRECTORY</c> environment variable, never as a command-line argument.
    ///     A failed hook leaves the case unpublished for operator recovery.
    /// </summary>
    internal static bool RunOnCrashHook(string evidenceDirectory, string? onCrashCommand, string scriptDirectory)
    {
        if (string.IsNullOrWhiteSpace(onCrashCommand)) return true;

        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var hookScript = Path.Combine(scriptDirectory,
                isWindows ? "on-crash.cmd" : "on-crash.sh");
            if (!File.Exists(hookScript))
            {
                GeneralTracer.Warn(
                    $"SupervisorLoop: on-crash hook script not found at {hookScript} — hook not run.");
                return false;
            }

            GeneralTracer.Info($"SupervisorLoop: on-crash hook starting ({onCrashCommand}).");
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                Arguments = isWindows ? "/d /c .\\on-crash.cmd" : "on-crash.sh",
                WorkingDirectory = scriptDirectory,
                UseShellExecute = false,
            };
            startInfo.Environment["BOWL_EVIDENCE_DIRECTORY"] = evidenceDirectory;
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                GeneralTracer.Warn("SupervisorLoop: on-crash hook could not be started.");
                return false;
            }

            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                GeneralTracer.Warn("SupervisorLoop: on-crash hook timed out after 60s and was killed.");
                return false;
            }
            else if (process.ExitCode != 0)
            {
                GeneralTracer.Warn($"SupervisorLoop: on-crash hook failed (code {process.ExitCode}).");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            GeneralTracer.Error("SupervisorLoop: on-crash hook failed.", ex);
            return false;
        }
    }
}
