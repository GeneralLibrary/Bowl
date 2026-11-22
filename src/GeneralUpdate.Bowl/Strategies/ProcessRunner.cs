using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GeneralUpdate.Bowl;

namespace GeneralUpdate.Bowl.Strategies;

/// <summary>
/// Async wrapper for running a child process with stdout/stderr capture and a timeout.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Starts the process described by <paramref name="startInfo"/>, captures output lines,
    /// and waits for exit or timeout.
    /// </summary>
    /// <param name="startInfo">Process start configuration.</param>
    /// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output lines.</returns>
    public static async Task<ProcessExitResult> RunAsync(
        ProcessStartInfo startInfo, int timeoutMs, CancellationToken ct = default)
    {
        GeneralTracer.Info($"ProcessRunner.RunAsync: starting '{startInfo.FileName} {startInfo.Arguments}'");

        var outputLines = new List<string>();

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                stdoutClosed.TrySetResult(true);
            else if (e.Data.Length > 0)
            {
                GeneralTracer.Debug($"ProcessRunner: {e.Data}");
                lock (outputLines) outputLines.Add(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                stderrClosed.TrySetResult(true);
            else if (e.Data.Length > 0)
            {
                GeneralTracer.Debug($"ProcessRunner(stderr): {e.Data}");
                lock (outputLines) outputLines.Add(e.Data);
            }
        };

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start())
        {
            GeneralTracer.Fatal("ProcessRunner.RunAsync: failed to start process.");
            throw new InvalidOperationException("Failed to start process.");
        }

        GeneralTracer.Info($"ProcessRunner.RunAsync: process started, PID={process.Id}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Exited can fire before the redirected streams have been drained.
        var completionTask = Task.WhenAll((Task)tcs.Task, stdoutClosed.Task, stderrClosed.Task);
        // Wait for exit and stream completion or timeout/cancellation.
        // Cancel the delay when the process exits first to avoid a timer leak.
        var delayTask = Task.Delay(timeoutMs, timeoutCts.Token);
        var completedTask = await Task.WhenAny(completionTask, delayTask);

        // Cancel the opposing task so timers/resources are reclaimed promptly.
        timeoutCts.Cancel();

        if (completedTask == completionTask)
        {
            try { await delayTask; } catch (OperationCanceledException) { /* cancelled — expected */ }
            await completionTask;
            var exitCode = await tcs.Task;
            GeneralTracer.Info($"ProcessRunner.RunAsync: process exited, ExitCode={exitCode}");
            IReadOnlyList<string> snapshot;
            lock (outputLines) { snapshot = outputLines.ToArray(); }
            return new ProcessExitResult { ExitCode = exitCode, OutputLines = snapshot };
        }

        // Timeout or cancellation — stop the process if it is still running.
        GeneralTracer.Warn(ct.IsCancellationRequested
            ? "ProcessRunner.RunAsync: cancellation requested."
            : $"ProcessRunner.RunAsync: process or output capture timed out after {timeoutMs}ms.");
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception ex)
        {
            GeneralTracer.Error("ProcessRunner.RunAsync: error killing process.", ex);
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"Process '{startInfo.FileName}' or its output capture did not complete within {timeoutMs}ms.");
    }
}
