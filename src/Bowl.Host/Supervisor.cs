using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace Bowl.Host;

internal sealed class Supervisor(IProcesses processes, Delivery delivery, TimeSpan? updaterExitWait = null)
{
    private static readonly HashSet<string> Stages =
        ["preparing", "filesApplying", "filesApplied", "launching", "awaitingHealth", "completed", "failed"];

    public async Task<Result> RunAsync(string root, string attempt, CancellationToken token)
    {
        root = Paths.Absolute(root);
        if (!Guid.TryParseExact(attempt, "D", out var id) || id.ToString("D") != attempt)
            throw new InvalidDataException("Attempt must be a canonical GUID.");
        var directory = Path.Combine(root, "attempts", attempt);
        Paths.NoLinks(directory);
        using var ownership = Disk.Lock(directory);
        var request = Disk.Read(Path.Combine(directory, "request.json"), ProtocolJson.Default.Request)
                      ?? throw new InvalidDataException("Missing immutable request.");
        Disk.Validate(request, attempt);
        Paths.Validate(request, root);
        var resultPath = Path.Combine(directory, "result.json");
        var result = Disk.Read(resultPath, ProtocolJson.Default.Result);
        // Final reporting does not take installation ownership. Recovery does,
        // so a delayed retry cannot overwrite a newer update of the same app.
        using var installationOwnership = result == null ||
            result.Rollback is "pending" or "inProgress" or "deferred"
            ? Disk.LockInstallation(root, request.InstallPath) : null;
        if (installationOwnership != null)
            ClaimInstallation(installationOwnership.Name + ".owner.json", root, directory, request, result);
        if (result == null)
        {
            // Admission is bounded and never evicts pending evidence. Operators
            // archive acknowledged attempts outside this tree before it fills.
            if (Directory.EnumerateDirectories(Path.Combine(root, "attempts")).Take(10_001).Count() > 10_000)
                throw new InvalidDataException("Attempt capacity exhausted; archive acknowledged history.");
            // Probe access before the readiness promise. A dead updater is still
            // a recoverable attempt, but inability to inspect it is not readiness.
            _ = processes.IsAlive(request.Updater);
            Disk.Write(Path.Combine(directory, "ready.json"), new Ready
            {
                AttemptId = attempt, Host = ProcessIdentity.Current()
            }, ProtocolJson.Default.Ready);
            result = await ObserveAsync(directory, request, token);
            Disk.Write(resultPath, result, ProtocolJson.Default.Result);
        }
        Disk.Validate(result, attempt);
        if (result.EventId != attempt + "-terminal-v1" || result.Updater != request.Updater ||
            result.Outcome is not ("success" or "updateFailure" or "launchFailure" or
                "healthCheckFailure" or "updaterTerminated" or "updateTimeout" or "monitorUnavailable") ||
            result.Rollback is not ("notRequested" or "pending" or "inProgress" or "deferred" or "succeeded" or "failed"))
            throw new InvalidDataException("Persisted result does not match request.");

        result = await RecoverAsync(directory, request, result, token);
        Disk.Write(resultPath, result, ProtocolJson.Default.Result);
        // Deferred recovery is durable but not reportable yet. No network work
        // is started before rollback is either finished or explicitly failed.
        if (result.Rollback is not ("pending" or "inProgress" or "deferred"))
            await delivery.DeliverAsync(directory, request, result, token);
        return result;
    }

    private void ClaimInstallation(string ownerPath, string root, string directory, Request request, Result? result)
    {
        var owner = Disk.Read(ownerPath, ProtocolJson.Default.Envelope);
        if (owner != null && owner.AttemptId != request.AttemptId)
        {
            if (owner.ProtocolVersion != 1 || !Guid.TryParseExact(owner.AttemptId, "D", out var previousId) ||
                previousId.ToString("D") != owner.AttemptId)
                throw new InvalidDataException("Invalid installation owner.");
            if (result != null || File.Exists(Path.Combine(directory, "ready.json")))
                throw new InvalidDataException("Superseded attempts cannot resume installation recovery.");
            var previousDirectory = Path.Combine(root, "attempts", owner.AttemptId);
            var previousRequest = Disk.Read(Path.Combine(previousDirectory, "request.json"), ProtocolJson.Default.Request);
            var previousResult = Disk.Read(Path.Combine(previousDirectory, "result.json"), ProtocolJson.Default.Result);
            if (previousRequest == null || previousResult == null)
                throw new InvalidDataException("Previous attempt needs explicit recovery before another update.");
            Disk.Validate(previousRequest, owner.AttemptId);
            Disk.Validate(previousResult, owner.AttemptId);
            if (processes.IsAlive(previousRequest.Updater) ||
                previousResult.Rollback is not ("notRequested" or "succeeded" or "failed"))
                throw new InvalidDataException("Previous updater or rollback still owns this installation.");
        }
        Disk.Write(ownerPath, new Envelope { AttemptId = request.AttemptId }, ProtocolJson.Default.Envelope);
    }

    private async Task<Result> ObserveAsync(string directory, Request request, CancellationToken token)
    {
        Producer? producer = null;
        ProcessIdentity? observedApplication = null;
        Stopwatch? health = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                producer = ReadProducer(directory, request);
                var updaterAlive = processes.IsAlive(request.Updater);
                // Read again after exit to consume the producer's final atomic
                // handoff rather than racing its last write.
                if (!updaterAlive)
                    producer = ReadProducer(directory, request);
                if (producer?.Stage == "failed")
                {
                    var category = producer.Error?.Category;
                    return Finish(request, producer, category is "monitorUnavailable" or "launchFailure"
                        ? category : "updateFailure");
                }
                if (request.LaunchMode == "filesOnly" && producer?.Stage == "completed")
                    return Finish(request, producer, "success", "filesApplied");
                if (request.LaunchMode == "processAlive" && producer?.Stage == "awaitingHealth")
                {
                    var application = producer.Application
                        ?? throw new InvalidDataException("Application identity is required.");
                    if (application.Pid == request.Updater.Pid || application.Pid == Environment.ProcessId)
                        throw new InvalidDataException("Application cannot be the updater or monitor.");
                    if (observedApplication != null && observedApplication != application)
                        return Finish(request, producer, "healthCheckFailure");
                    observedApplication = application;
                    if (!processes.IsAlive(application))
                        return Finish(request, producer, "healthCheckFailure");
                    health ??= Stopwatch.StartNew();
                    if (health.Elapsed >= TimeSpan.FromSeconds(request.HealthTimeoutSeconds))
                        return Finish(request, producer, "success", "processAlive");
                }
                else
                {
                    if (health != null)
                        throw new InvalidDataException("Producer regressed after application handoff.");
                    if (!updaterAlive)
                        return Finish(request, producer, "updaterTerminated");
                    if (DateTimeOffset.UtcNow - request.CreatedAtUtc >
                        TimeSpan.FromSeconds(request.UpdateTimeoutSeconds))
                        return Finish(request, producer, "updateTimeout");
                }
                await Task.Delay(100, token);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or Win32Exception or
                                       UnauthorizedAccessException or InvalidOperationException)
        {
            // A broken observer is never evidence that the application failed.
            Console.Error.WriteLine($"Monitoring unavailable: {ex.GetType().Name}.");
            return Finish(request, producer, "monitorUnavailable") with
            {
                Error = new Failure { Category = "monitorUnavailable", ExceptionType = ex.GetType().FullName }
            };
        }
    }

    private static Producer? ReadProducer(string directory, Request request)
    {
        var producer = Disk.Read(Path.Combine(directory, "producer.json"), ProtocolJson.Default.Producer);
        if (producer != null)
        {
            Disk.Validate(producer, request.AttemptId);
            if (!Stages.Contains(producer.Stage) || producer.UpdatedAtUtc == default)
                throw new InvalidDataException("Invalid producer phase.");
        }
        return producer;
    }

    private static Result Finish(Request request, Producer? producer, string outcome, string verification = "none")
    {
        var error = producer?.Error;
        if (error != null && request.Report.CredentialEnvironmentVariable is { Length: > 0 } name &&
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } secret)
        {
            string? Redact(string? text) => text?.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            error = error with
            {
                Message = Redact(error.Message), StackTrace = Redact(error.StackTrace),
                FailedPath = Redact(error.FailedPath), ExceptionType = Redact(error.ExceptionType),
                Stage = Redact(error.Stage), Category = Redact(error.Category)!
            };
        }
        return new Result
        {
            AttemptId = request.AttemptId, EventId = request.AttemptId + "-terminal-v1",
            Host = ProcessIdentity.Current(), Updater = request.Updater,
            Application = producer?.Application, Outcome = outcome,
            Stage = producer?.Stage ?? "preparing", Verification = verification,
            CurrentVersion = request.CurrentVersion, TargetVersion = request.TargetVersion,
            ObservedAtUtc = DateTimeOffset.UtcNow, Error = error,
            Rollback = request.AutoRollback && outcome is not ("success" or "monitorUnavailable")
                ? "pending" : "notRequested"
        };
    }

    private async Task<Result> RecoverAsync(string directory, Request request, Result result, CancellationToken token)
    {
        if (result.Rollback is not ("pending" or "inProgress" or "deferred"))
            return result;
        try
        {
            // A timeout/failure is NOT permission to race an updater still
            // writing files. Re-run this attempt after it exits to resume.
            var wait = Stopwatch.StartNew();
            while (processes.IsAlive(request.Updater))
            {
                if (wait.Elapsed >= (updaterExitWait ?? TimeSpan.FromSeconds(10)))
                    return result with { Rollback = "deferred", RollbackError = "UpdaterStillRunning" };
                await Task.Delay(100, token);
            }
            var latest = ReadProducer(directory, request);
            var application = latest?.Application ?? result.Application;
            if (application == null && (latest?.Stage ?? result.Stage) is "launching" or "awaitingHealth")
                throw new InvalidDataException("Cannot fence an application without its identity.");
            if (application != null)
            {
                if (application.Pid == Environment.ProcessId || application.Pid == request.Updater.Pid)
                    throw new InvalidDataException("Unsafe application identity.");
                await processes.StopAsync(application, token);
            }
            result = result with { Rollback = "inProgress", RollbackError = null, Application = application };
            Disk.Write(Path.Combine(directory, "result.json"), result, ProtocolJson.Default.Result);
            Recovery.Restore(request.BackupDirectory!, request.InstallPath);
            return result with { Rollback = "succeeded" };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or
                                       Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Rollback failed: {ex.GetType().Name}.");
            return result with { Rollback = "failed", RollbackError = ex.GetType().Name };
        }
    }
}
