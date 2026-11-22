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
    public async Task FailedRecoveryBlocksNextAttemptUntilExplicitRepair()
    {
        using var f = new Fixture();
        f.Request = f.Request with { AutoRollback = true };
        f.Processes.Alive = false;
        f.Snapshot();
        f.Producer("failed", error: new Failure { Category = "updateFailure" });
        Assert.Equal("failed", (await f.Run()).Rollback);
        f.Request = f.Request with { AttemptId = Guid.NewGuid().ToString("D") };
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

    [Fact]
    public async Task FailedUpdateReportsFailedRollbackAndRetriesIdenticalSafeDiagnostics()
    {
        using var f = new Fixture();
        var variable = "BOWL_APP_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "example-app-secret");
        try
        {
            f.Request = f.Request with
            {
                AutoRollback = true,
                Report = f.Request.Report with { AppKeyEnvironmentVariable = variable, Url = "https://localhost/report" }
            };
            f.Processes.Alive = false;
            f.Snapshot();
            f.Producer("failed", error: new Failure
            {
                Category = "updateFailure", ExceptionType = "System.IO.IOException",
                Message = "private message", StackTrace = "private stack", FailedPath = "private path", HResult = -123
            });
            f.Handler.Status = HttpStatusCode.ServiceUnavailable;
            f.Handler.BeforeSend = () => Assert.Equal("new",
                File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));

            var result = await f.Run();
            Assert.Equal("failed", result.Rollback);
            Assert.Equal("pending", f.Outbox.State);
            var first = Assert.Single(f.Handler.Bodies);
            using var body = JsonDocument.Parse(first);
            Assert.Equal("example-app-secret", body.RootElement.GetProperty("appKey").GetString());
            Assert.DoesNotContain("example-app-secret", File.ReadAllText(Path.Combine(f.DirectoryPath, "request.json")));
            Assert.DoesNotContain("example-app-secret", File.ReadAllText(Path.Combine(f.DirectoryPath, "outbox.json")));
            var diagnostics = body.RootElement.GetProperty("diagnostics");
            Assert.Equal("updateFailure", diagnostics.GetProperty("outcome").GetString());
            Assert.Equal("failed", diagnostics.GetProperty("stage").GetString());
            Assert.False(diagnostics.GetProperty("rollback").GetBoolean());
            Assert.Equal("System.IO.IOException", diagnostics.GetProperty("exceptionType").GetString());
            Assert.Equal(-123, diagnostics.GetProperty("hResult").GetInt32());
            Assert.DoesNotContain("private", first);

            Disk.Write(Path.Combine(f.DirectoryPath, "outbox.json"),
                f.Outbox with { NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, ProtocolJson.Default.Outbox);
            f.Handler.Status = HttpStatusCode.OK;
            f.Handler.ResponseBody = """{"code":200,"body":true,"isSuccess":true}""";
            await f.Run();
            Assert.Equal(2, f.Handler.Bodies.Count);
            Assert.Equal(first, f.Handler.Bodies[1]);
            Assert.Equal(f.Handler.Keys[0], f.Handler.Keys[1]);
            Assert.Equal("acknowledged", f.Outbox.State);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task ExistingLegacyOutboxRetriesWithoutChangingPayload()
    {
        using var f = new Fixture();
        f.Producer("failed", error: new Failure { Category = "updateFailure" });
        f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        await f.Run();
        var legacy = f.Outbox with
        {
            Payload = new LegacyPayload(42, 3, 1),
            NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        Disk.Write(Path.Combine(f.DirectoryPath, "outbox.json"), legacy, ProtocolJson.Default.Outbox);
        f.Handler.Status = HttpStatusCode.OK;
        await f.Run();
        using var body = JsonDocument.Parse(f.Handler.Bodies[1]);
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        Assert.Equal("acknowledged", f.Outbox.State);
    }

    [Fact]
    public async Task UntrustedExceptionTypeIsNotUploaded()
    {
        using var f = new Fixture();
        f.Producer("failed", error: new Failure
        {
            Category = "updateFailure", ExceptionType = "Private.Customer.Secret"
        });
        await f.Run();
        Assert.DoesNotContain("Private.Customer.Secret", Assert.Single(f.Handler.Bodies));
    }

    [Theory]
    [InlineData("{\"code\":400,\"body\":false,\"isSuccess\":false}", "pending")]
    [InlineData("{\"code\":200,\"body\":true,\"isSuccess\":false}", "pending")]
    [InlineData("{\"code\":200,\"body\":false}", "pending")]
    [InlineData("{\"code\":200,\"body\":true,\"isSuccess\":true,\"errorCode\":0}", "acknowledged")]
    [InlineData("{\"unexpected\":true}", "pending")]
    [InlineData("invalid", "pending")]
    public async Task BusinessAcknowledgementMustIndicateSuccess(string response, string state)
    {
        using var f = new Fixture();
        f.Handler.ResponseBody = response;
        f.Producer("completed");
        await f.Run();
        Assert.Equal(state, f.Outbox.State);
    }

    [Fact]
    public async Task RetryWithoutAppKeyKeepsPendingAndRecordsError()
    {
        using var f = new Fixture();
        var variable = "BOWL_MISSING_" + Guid.NewGuid().ToString("N");
        f.Request = f.Request with
        {
            Report = f.Request.Report with { AppKeyEnvironmentVariable = variable }
        };
        f.Producer("failed", error: new Failure { Category = "updateFailure" });
        await f.Run();
        Assert.Empty(f.Handler.Bodies);
        Assert.Equal("pending", f.Outbox.State);
        Assert.Equal("AppKeyUnavailable", f.Outbox.LastError);
        Disk.Write(Path.Combine(f.DirectoryPath, "outbox.json"),
            f.Outbox with { NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, ProtocolJson.Default.Outbox);
        Assert.Equal(0, await Program.Main(["--retry", "1", "--state-root", f.Root]));
        Assert.Equal("pending", f.Outbox.State);
        Assert.Equal(2, f.Outbox.Attempts);
        Assert.Equal("AppKeyUnavailable", f.Outbox.LastError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"code\":200,\"body\":true}")]
    public async Task AppKeyReportRequiresFullBusinessAcknowledgement(string response)
    {
        using var f = new Fixture();
        var variable = "BOWL_APP_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-credential");
        try
        {
            f.Request = f.Request with
            {
                Report = f.Request.Report with
                {
                    Url = "https://localhost/report", AppKeyEnvironmentVariable = variable
                }
            };
            f.Handler.ResponseBody = response;
            f.Producer("completed");
            await f.Run();
            Assert.Equal("pending", f.Outbox.State);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("http://127.0.0.1/report", true)]
    [InlineData("http://example.invalid/report", false)]
    [InlineData("https://example.invalid/report", true)]
    public async Task AppKeyRequiresHttpsExceptLoopback(string url, bool accepted)
    {
        using var f = new Fixture();
        var variable = "BOWL_APP_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-credential");
        try
        {
            f.Request = f.Request with { Report = f.Request.Report with
            {
                Url = url, AppKeyEnvironmentVariable = variable
            }};
            f.Handler.ResponseBody = """{"code":200,"body":true,"isSuccess":true}""";
            f.Producer("completed");
            await f.Run();
            Assert.Equal(accepted ? "acknowledged" : "pending", f.Outbox.State);
            Assert.Equal(accepted ? 1 : 0, f.Handler.Bodies.Count);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
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
    public async Task CompletedHandoffWithoutObservedAwaitingHealth_IsSupervised()
    {
        // The protocol allows the updater to write 'completed' and exit; two writes can
        // land inside one poll so the host may never see awaitingHealth. The exited
        // updater must not be mislabeled updaterTerminated (which with AutoRollback
        // would stop the healthy application and restore the old files).
        using var f = new Fixture();
        f.Request = f.Request with { LaunchMode = "processAlive" };
        var app = new ProcessIdentity(54321, DateTimeOffset.UtcNow);
        f.Processes.Alive = false;                       // updater already exited
        f.Processes.Probe = identity => identity == app; // only the application is alive
        f.Producer("completed", app);
        var result = await f.Run();
        Assert.Equal("success", result.Outcome);
        Assert.Equal("processAlive", result.Verification);
    }

    [Fact]
    public async Task CompletedDuringHealthObservation_IsNotRegression()
    {
        // A producer finalizing with 'completed' while the host is already inside the
        // health window is a legal final confirmation, not a regression; discarding the
        // observation used to report monitorUnavailable for a healthy update.
        using var f = new Fixture();
        f.Request = f.Request with { LaunchMode = "processAlive" };
        var app = new ProcessIdentity(54321, DateTimeOffset.UtcNow);
        f.Processes.Probe = identity =>
        {
            if (identity != app) f.Producer("completed", app); // finalize after handoff
            return identity == app;
        };
        f.Producer("awaitingHealth", app);
        var result = await f.Run();
        Assert.Equal("success", result.Outcome);
        Assert.Equal("processAlive", result.Verification);
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
    public async Task TimeoutDefersRollbackUntilUpdaterExits_ThenReportsSafetyFailure()
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
            Assert.Equal("new", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
            Assert.True(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
        };
        f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        result = await f.Run();
        Assert.Equal("failed", result.Rollback);
        Assert.Equal("RecoveryPlanException", result.RollbackError);
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
        Assert.Equal("RecoveryPlanException", result.RollbackError);
        Assert.Equal("acknowledged", f.Outbox.State);
        Assert.Equal(3, f.Outbox.Payload.Status);
    }

    [Fact]
    public async Task UnavailableRollbackDoesNotStopIdentifiedApplication()
    {
        using var f = new Fixture();
        f.Request = f.Request with { AutoRollback = true, LaunchMode = "processAlive" };
        f.Processes.Alive = false;
        f.Snapshot();
        var app = new ProcessIdentity(54321, DateTimeOffset.UtcNow);
        f.Producer("awaitingHealth", app);
        Assert.Equal("failed", (await f.Run()).Rollback);
        Assert.Empty(f.Processes.Stopped);
    }

    [Fact]
    public async Task MissingPlanDoesNotStopApplication()
    {
        using var f = new Fixture();
        f.Request = f.Request with
        {
            AutoRollback = true, LaunchMode = "processAlive", RollbackPlan = null
        };
        var application = new ProcessIdentity(54321, DateTimeOffset.UtcNow);
        f.Processes.Alive = false;
        f.Snapshot();
        f.Producer("awaitingHealth", application);
        var result = await f.Run();
        Assert.Equal("failed", result.Rollback);
        Assert.Equal("RecoveryPlanException", result.RollbackError);
        Assert.Empty(f.Processes.Stopped);
    }

    [Fact]
    public async Task InterruptedRollbackIsRefusedWithoutModifyingRemainingFiles()
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
        Assert.Equal("failed", (await f.Run()).Rollback);
        Assert.False(File.Exists(Path.Combine(f.Request.InstallPath, "application.txt")));
        Assert.True(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
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
