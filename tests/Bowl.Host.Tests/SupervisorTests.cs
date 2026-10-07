using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class SupervisorTests
{
    [Fact]
    public async Task DeferredRecoveryBlocksAnotherAttemptForSameInstallation()
    {
        using var f = new Fixture();
        f.Request = f.Request with { CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), AutoRollback = true };
        f.Snapshot();
        f.Producer("filesApplying");
        Assert.Equal("deferred", (await f.Run()).Rollback);
        f.Request = f.Request with { AttemptId = Guid.NewGuid().ToString("D"), CreatedAtUtc = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(f.DirectoryPath);
        f.Producer("completed");
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
    }

    [Fact]
    public async Task NewAttemptWaitsForPreviousUpdaterEvenAfterFileSuccess()
    {
        using var f = new Fixture();
        f.Producer("completed");
        Assert.Equal("success", (await f.Run()).Outcome);
        f.Request = f.Request with { AttemptId = Guid.NewGuid().ToString("D") };
        Directory.CreateDirectory(f.DirectoryPath);
        f.Producer("completed");
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
        f.Processes.Alive = false;
        Assert.Equal("success", (await f.Run()).Outcome);
    }

    [Fact]
    public async Task SameInstallationCannotHaveTwoLiveOwners()
    {
        using var f = new Fixture();
        using var ownership = Disk.LockInstallation(f.Root, f.Request.InstallPath);
        f.Producer("completed");
        await Assert.ThrowsAsync<IOException>(f.Run);
    }

    [Fact]
    public async Task FilesOnly_ReportsExactLegacyPayload_AndRestartDoesNotResend()
    {
        using var f = new Fixture();
        f.Producer("completed");
        var result = await f.Run();
        Assert.Equal("success", result.Outcome);
        Assert.Equal("filesApplied", result.Verification);
        using var body = JsonDocument.Parse(Assert.Single(f.Handler.Bodies));
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        Assert.Equal(42, body.RootElement.GetProperty("recordId").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(result.EventId, Assert.Single(f.Handler.Keys));
        Assert.Equal("acknowledged", f.Outbox.State);
        await f.Run();
        Assert.Single(f.Handler.Bodies);
    }

    [Theory]
    [InlineData("updateFailure")]
    [InlineData("launchFailure")]
    [InlineData("monitorUnavailable")]
    public async Task ProducerFailure_PreservesCategoryAndOriginalEvidence(string category)
    {
        using var f = new Fixture();
        f.Producer("failed", error: new Failure
        {
            Category = category, ExceptionType = "OriginalException", Message = "original", HResult = 123
        });
        var result = await f.Run();
        Assert.Equal(category, result.Outcome);
        Assert.Equal("OriginalException", result.Error!.ExceptionType);
        Assert.Equal(123, result.Error.HResult);
        if (category == "monitorUnavailable")
        {
            Assert.Empty(f.Handler.Bodies);
            Assert.Equal("localOnly", f.Outbox.State);
        }
        else
            Assert.Equal(3, f.Outbox.Payload.Status);
    }

    [Fact]
    public async Task UpdaterExitWithoutDump_IsFailure()
    {
        using var f = new Fixture();
        f.Processes.Alive = false;
        f.Producer("filesApplying");
        var result = await f.Run();
        Assert.Equal("updaterTerminated", result.Outcome);
        Assert.Equal("none", result.Verification);
    }

    [Fact]
    public async Task CompletedFilesOnlySurvivesUpdaterExit()
    {
        using var f = new Fixture();
        f.Processes.Alive = false;
        f.Producer("completed");
        Assert.Equal("success", (await f.Run()).Outcome);
    }

    [Fact]
    public async Task ReReadsHandoffAfterUpdaterExit()
    {
        using var f = new Fixture();
        f.Producer("filesApplying");
        f.Processes.Probe = _ => { f.Producer("completed"); return false; };
        Assert.Equal("success", (await f.Run()).Outcome);
    }

    [Fact]
    public async Task SurvivalRequiresTheEntireWindow()
    {
        using var f = new Fixture();
        f.Request = f.Request with { LaunchMode = "processAlive" };
        var app = new ProcessIdentity(54321, DateTimeOffset.UtcNow);
        f.Processes.Probe = identity => identity == app;
        f.Producer("awaitingHealth", app);
        var timer = Stopwatch.StartNew();
        var result = await f.Run();
        Assert.Equal("success", result.Outcome);
        Assert.Equal("processAlive", result.Verification);
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EarlyApplicationExit_IsHealthFailureNotLaunchOrUpdateFailure()
    {
        using var f = new Fixture();
        f.Request = f.Request with { LaunchMode = "processAlive" };
        f.Processes.Alive = false;
        f.Producer("awaitingHealth", new(54321, DateTimeOffset.UtcNow));
        Assert.Equal("healthCheckFailure", (await f.Run()).Outcome);
        Assert.Equal(3, f.Outbox.Payload.Status);
    }

    [Fact]
    public async Task MissingApplicationIdentity_IsMonitoringUnavailable()
    {
        using var f = new Fixture();
        f.Request = f.Request with { LaunchMode = "processAlive" };
        f.Producer("awaitingHealth");
        Assert.Equal("monitorUnavailable", (await f.Run()).Outcome);
        Assert.Empty(f.Handler.Bodies);
    }

    [Fact]
    public async Task FilesAppliedAloneCannotVerifyHealth()
    {
        using var f = new Fixture();
        f.Request = f.Request with { LaunchMode = "processAlive" };
        f.Producer("filesApplied");
        f.Processes.Alive = false;
        Assert.Equal("updaterTerminated", (await f.Run()).Outcome);
    }

    [Fact]
    public async Task TimeoutDefersRollbackUntilUpdaterExits_ThenRestoresBeforeNetwork()
    {
        using var f = new Fixture();
        f.Request = f.Request with { CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), AutoRollback = true };
        f.Snapshot();
        f.Producer("filesApplying");
        var result = await f.Run();
        Assert.Equal("updateTimeout", result.Outcome);
        Assert.Equal("deferred", result.Rollback);
        Assert.Empty(f.Handler.Bodies);
        Assert.Equal("new", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
        f.Processes.Alive = false;
        f.Handler.BeforeSend = () =>
        {
            Assert.Equal("old", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
            Assert.False(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
        };
        f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        result = await f.Run();
        Assert.Equal("succeeded", result.Rollback);
        Assert.True(File.Exists(Path.Combine(f.Request.BackupDirectory!, "application.txt")));
        Assert.Equal("pending", f.Outbox.State);
    }

    [Fact]
    public async Task MissingSnapshotIsExplicitFailure_NotClaimedAsRestored()
    {
        using var f = new Fixture();
        f.Request = f.Request with { AutoRollback = true };
        f.Processes.Alive = false;
        f.Producer("failed", error: new Failure { Category = "updateFailure" });
        var result = await f.Run();
        Assert.Equal("failed", result.Rollback);
        Assert.Equal("DirectoryNotFoundException", result.RollbackError);
        Assert.Equal("acknowledged", f.Outbox.State);
        Assert.Equal(3, f.Outbox.Payload.Status);
    }

    [Fact]
    public async Task RollbackStopsIdentifiedApplication()
    {
        using var f = new Fixture();
        f.Request = f.Request with { AutoRollback = true, LaunchMode = "processAlive" };
        f.Processes.Alive = false;
        f.Snapshot();
        var app = new ProcessIdentity(54321, DateTimeOffset.UtcNow);
        f.Producer("awaitingHealth", app);
        Assert.Equal("succeeded", (await f.Run()).Rollback);
        Assert.Equal(app, Assert.Single(f.Processes.Stopped));
    }

    [Fact]
    public async Task InterruptedRollbackResumesFromDurableJournal()
    {
        using var f = new Fixture();
        f.Request = f.Request with { AutoRollback = true };
        f.Snapshot();
        f.Producer("filesApplying");
        f.Request = f.Request with { CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var result = await f.Run();
        Disk.Write(Path.Combine(f.DirectoryPath, "result.json"),
            result with { Rollback = "inProgress" }, ProtocolJson.Default.Result);
        File.Delete(Path.Combine(f.Request.InstallPath, "application.txt"));
        f.Processes.Alive = false;
        Assert.Equal("succeeded", (await f.Run()).Rollback);
        Assert.Equal("old", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
    }

    [Fact]
    public async Task LostOutboxAfterResultIsRecreatedWithStableEventId()
    {
        using var f = new Fixture();
        f.Producer("completed");
        f.Handler.Status = HttpStatusCode.InternalServerError;
        var result = await f.Run();
        File.Delete(Path.Combine(f.DirectoryPath, "outbox.json"));
        f.Handler.Status = HttpStatusCode.OK;
        await f.Run();
        Assert.Equal(result.EventId, f.Outbox.EventId);
        Assert.Equal("acknowledged", f.Outbox.State);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task Non2xxRetained_OnlyAckAfterDueRetry(int status)
    {
        using var f = new Fixture();
        f.Producer("completed");
        f.Handler.Status = (HttpStatusCode)status;
        await f.Run();
        var pending = f.Outbox;
        Assert.Equal("pending", pending.State);
        Assert.Equal($"HTTP {status}", pending.LastError);
        Assert.True(pending.NextAttemptUtc > DateTimeOffset.UtcNow);
        await f.Run();
        Assert.Single(f.Handler.Bodies);
        Disk.Write(Path.Combine(f.DirectoryPath, "outbox.json"),
            pending with { NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, ProtocolJson.Default.Outbox);
        f.Handler.Status = HttpStatusCode.OK;
        await f.Run();
        Assert.Equal("acknowledged", f.Outbox.State);
        Assert.Equal(2, f.Outbox.Attempts);
        Assert.Single(f.Handler.Keys.Distinct());
    }

    [Fact]
    public async Task NoEndpointIsLocalOnly_NotNetworkSuccess()
    {
        using var f = new Fixture();
        f.Request = f.Request with { Report = new() };
        f.Producer("completed");
        await f.Run();
        Assert.Equal("localOnly", f.Outbox.State);
        Assert.Null(f.Outbox.AcknowledgedAtUtc);
        Assert.Empty(f.Handler.Bodies);
    }
}
