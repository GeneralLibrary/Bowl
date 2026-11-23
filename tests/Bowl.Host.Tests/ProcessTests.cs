using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ProcessTests
{
    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Bowl.slnx")))
                    return directory.FullName;
            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    private static string Configuration =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    // Launched via the string overload with a literal file name — no shell is
    // involved, and each path is quoted (quotes are illegal in Windows paths, so
    // no path can alter the command structure).
    private static Process Start(string dll, params string[] arguments)
    {
        var commandLine = "\"" + dll + "\"" +
            string.Concat(arguments.Select(argument => " \"" + argument + "\""));
        return Process.Start("dotnet", commandLine)!;
    }

    private static Process Peer(int milliseconds) => Start(Path.Combine(Repository, "tests", "Bowl.TestPeer",
        "bin", Configuration, "net8.0", "Bowl.TestPeer.dll"), milliseconds.ToString());

    private static Fixture ProcessFixture()
    {
        var fixture = new Fixture();
        fixture.Request = fixture.Request with { UpdateTimeoutSeconds = 120 };
        return fixture;
    }

    private static Process Host(Fixture f) => Start(Path.Combine(Repository, "src", "Bowl.Host",
        "bin", Configuration, "net8.0", "Bowl.Host.dll"),
        "--attempt", f.Request.AttemptId, "--state-root", f.Root);

    [Fact]
    public async Task RealHostCrashCanResumeObservationAndRefuseUnsafeRollback()
    {
        using var f = ProcessFixture();
        using var updater = Peer(120_000);
        f.Request = f.Request with { Updater = Identity(updater), AutoRollback = true, Report = new() };
        f.Snapshot();
        f.Save();
        f.Producer("filesApplying");
        using var first = Host(f);
        try
        {
            await Ready(f, first);
            first.Kill();
            await Wait(first);
            Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "result.json")));
            updater.Kill();
            await Wait(updater);
            using var resumed = Host(f);
            try
            {
                await Wait(resumed);
                var result = Disk.Read(Path.Combine(f.DirectoryPath, "result.json"), ProtocolJson.Default.Result)!;
                Assert.Equal("updaterTerminated", result.Outcome);
                Assert.Equal("failed", result.Rollback);
                Assert.Equal("new", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
                Assert.True(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
                Assert.True(File.Exists(Path.Combine(f.Request.BackupDirectory!, "application.txt")));
            }
            finally { Cleanup(resumed); }
        }
        finally { Cleanup(first); Cleanup(updater); }
    }

    [Fact]
    public async Task UncooperativeWriterSurvivesHostCrashAndRecoveryRemainsClosed()
    {
        using var f = ProcessFixture();
        using var updater = Peer(120_000);
        f.Request = f.Request with { Updater = Identity(updater), AutoRollback = true, Report = new() };
        f.Snapshot();
        f.Save();
        f.Producer("filesApplying");
        var target = Path.Combine(f.Request.InstallPath, "application.txt");
        var peerPath = Path.Combine(Repository, "tests", "Bowl.TestPeer", "bin", Configuration,
            "net8.0", "Bowl.TestPeer.dll");
        var writerStart = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { peerPath, "uncooperative-writer", target })
            writerStart.ArgumentList.Add(argument);
        using var writer = Process.Start(writerStart)!;
        using var first = Host(f);
        try
        {
            Assert.Equal("WRITER_READY",
                await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await Ready(f, first);
            await writer.StandardInput.WriteLineAsync("live-external-change");
            await writer.StandardInput.FlushAsync();
            Assert.Equal("WRITE_COMPLETE",
                await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("live-external-change", File.ReadAllText(target));
            first.Kill();
            await Wait(first);
            updater.Kill();
            await Wait(updater);
            await writer.StandardInput.WriteLineAsync("external-change");
            await writer.StandardInput.FlushAsync();
            Assert.Equal("WRITE_COMPLETE",
                await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));

            using (var resumed = Host(f))
            {
                try
                {
                    await Wait(resumed);
                    Assert.Equal(1, resumed.ExitCode);
                    var result = Disk.Read(Path.Combine(f.DirectoryPath, "result.json"), ProtocolJson.Default.Result)!;
                    Assert.Equal("updaterTerminated", result.Outcome);
                    Assert.Equal("failed", result.Rollback);
                    Assert.Equal("RecoveryPlanException", result.RollbackError);
                    Assert.Equal("external-change", File.ReadAllText(target));
                    Assert.True(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
                    Assert.Equal("old", File.ReadAllText(Path.Combine(f.Request.BackupDirectory!, "application.txt")));
                }
                finally { Cleanup(resumed); }
            }

            var oldAttempt = f.Request.AttemptId;
            f.Request = f.Request with { AttemptId = Guid.NewGuid().ToString("D"), CreatedAtUtc = DateTimeOffset.UtcNow };
            Directory.CreateDirectory(f.DirectoryPath);
            f.Save();
            f.Producer("completed");
            using var next = Host(f);
            try
            {
                await Wait(next);
                Assert.Equal(2, next.ExitCode);
                Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
                Assert.True(File.Exists(Path.Combine(f.Root, "attempts", oldAttempt, "result.json")));
                Assert.Equal("external-change", File.ReadAllText(target));
                Assert.False(writer.HasExited);
            }
            finally { Cleanup(next); }
        }
        finally { Cleanup(first); Cleanup(updater); Cleanup(writer); }
    }

    private static ProcessIdentity Identity(Process process) => new(process.Id, process.StartTime.ToUniversalTime());

    [Fact]
    public async Task LiveUpdaterAndConcurrentUserChangesAreNotOverwritten()
    {
        using var f = ProcessFixture();
        using var updater = Peer(120_000);
        f.Request = f.Request with { Updater = Identity(updater), AutoRollback = true, Report = new() };
        f.Snapshot();
        f.Save();
        f.Producer("failed", error: new Failure { Category = "updateFailure" });
        using var host = Host(f);
        try
        {
            await Ready(f, host);
            File.WriteAllText(Path.Combine(f.Request.InstallPath, "application.txt"), "user-modified");
            File.WriteAllText(Path.Combine(f.Request.InstallPath, "unknown.txt"), "user-data");
            Assert.False(host.HasExited);
            updater.Kill();
            await Wait(updater);
            await Wait(host);
            var result = Disk.Read(Path.Combine(f.DirectoryPath, "result.json"), ProtocolJson.Default.Result)!;
            Assert.Equal("failed", result.Rollback);
            Assert.Equal("RecoveryPlanException", result.RollbackError);
            Assert.Equal("user-modified", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
            Assert.Equal("user-data", File.ReadAllText(Path.Combine(f.Request.InstallPath, "unknown.txt")));
            Assert.True(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
        }
        finally { Cleanup(host); Cleanup(updater); }
    }

    [Fact]
    public async Task RealHostRefusesUnsafeRollbackAndReportsFailureToLoopbackServer()
    {
        using var f = ProcessFixture();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var variable = "BOWL_REAL_APP_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "real-process-test-secret");
        using var updater = Peer(120_000);
        try
        {
            f.Request = f.Request with
            {
                Updater = Identity(updater), AutoRollback = true,
                Report = new()
                {
                    Url = $"http://127.0.0.1:{port}/report", RecordId = 42,
                    AppKeyEnvironmentVariable = variable
                }
            };
            f.Snapshot();
            Directory.CreateDirectory(Path.Combine(f.Request.InstallPath, "data"));
            File.WriteAllText(Path.Combine(f.Request.InstallPath, "data", "user.db"), "current-user-data");
            Directory.CreateDirectory(Path.Combine(f.Request.BackupDirectory!, "data"));
            File.WriteAllText(Path.Combine(f.Request.BackupDirectory!, "data", "user.db"), "old-user-data");
            f.Request = f.Request with
            {
                RollbackPlan = f.Request.RollbackPlan! with { ProtectedPaths = ["data"] }
            };
            f.Save();
            f.Producer("failed", error: new Failure
            {
                Category = "updateFailure", ExceptionType = "System.IO.IOException", HResult = -123
            });
            updater.Kill();
            await Wait(updater);
            using var host = Host(f);
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal("new", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
                Assert.True(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
                Assert.Equal("current-user-data", File.ReadAllText(Path.Combine(f.Request.InstallPath, "data", "user.db")));
                using var reader = new StreamReader(context.Request.InputStream);
                using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                Assert.Equal("real-process-test-secret", json.RootElement.GetProperty("appKey").GetString());
                Assert.False(json.RootElement.GetProperty("diagnostics").GetProperty("rollback").GetBoolean());
                var response = Encoding.UTF8.GetBytes("""{"code":200,"body":true,"isSuccess":true}""");
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response);
                context.Response.Close();
                await Wait(host);
                Assert.Equal(1, host.ExitCode);
                Assert.Equal("acknowledged", f.Outbox.State);
                Assert.DoesNotContain("real-process-test-secret",
                    File.ReadAllText(Path.Combine(f.DirectoryPath, "outbox.json")));
            }
            finally { Cleanup(host); }
        }
        finally
        {
            Cleanup(updater);
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static async Task Wait(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static void Cleanup(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit(5000);
        }
    }

    private static async Task<Ready> Ready(Fixture f, Process host)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            var ready = Disk.Read(Path.Combine(f.DirectoryPath, "ready.json"), ProtocolJson.Default.Ready);
            if (ready != null)
                return ready;
            Assert.False(host.HasExited, await ReadErrorIfExited(host));
            await Task.Delay(25);
        }
        throw new TimeoutException("Host did not publish readiness.");
    }

    private static Task<string> ReadErrorIfExited(Process process) => Task.FromResult(process.HasExited
        ? $"Host exited prematurely (code {process.ExitCode})." : "Host still running");

    [Fact]
    public async Task RealHostReadyIsSamePidAndStartTime_ThenConfirmsFiles()
    {
        using var f = ProcessFixture();
        f.Request = f.Request with { Updater = ProcessIdentity.Current(), Report = new() };
        f.Save();
        using var host = Host(f);
        try
        {
            var ready = await Ready(f, host);
            // On Linux, Process.StartTime is re-derived per read with ~1s of boot-time
            // jitter, so the identity is compared with a tolerance off Windows.
            Assert.Equal(host.Id, ready.Host.Pid);
            var startDrift = Math.Abs((ready.Host.StartTimeUtc - Identity(host).StartTimeUtc).TotalSeconds);
            Assert.True(startDrift <= 2,
                $"Host start time drifted by {startDrift:F3}s: {ready.Host.StartTimeUtc} vs {Identity(host).StartTimeUtc}.");
            Assert.Equal(f.Request.AttemptId, ready.AttemptId);
            f.Producer("completed");
            await Wait(host);
            Assert.Equal(0, host.ExitCode);
            Assert.Equal("filesApplied", Disk.Read(Path.Combine(f.DirectoryPath, "result.json"),
                ProtocolJson.Default.Result)!.Verification);
        }
        finally { Cleanup(host); }
    }

    [Fact]
    public async Task RealUpdaterTerminationWithoutDumpIsRecorded()
    {
        using var f = ProcessFixture();
        using var updater = Peer(120_000);
        f.Request = f.Request with { Updater = Identity(updater), Report = new() };
        f.Save();
        f.Producer("filesApplying");
        using var host = Host(f);
        try
        {
            await Ready(f, host);
            updater.Kill();
            await Wait(updater);
            await Wait(host);
            Assert.Equal(1, host.ExitCode);
            Assert.Equal("updaterTerminated", Disk.Read(Path.Combine(f.DirectoryPath, "result.json"),
                ProtocolJson.Default.Result)!.Outcome);
        }
        finally { Cleanup(host); Cleanup(updater); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealApplicationSurvivalAndEarlyExit(bool exitEarly)
    {
        using var f = ProcessFixture();
        using var application = Peer(120_000);
        f.Request = f.Request with
        {
            Updater = ProcessIdentity.Current(), LaunchMode = "processAlive", Report = new()
        };
        f.Save();
        using var host = Host(f);
        try
        {
            await Ready(f, host);
            var timer = Stopwatch.StartNew();
            f.Producer("awaitingHealth", Identity(application));
            if (exitEarly)
            {
                await Task.Delay(150);
                application.Kill();
            }
            await Wait(host);
            var result = Disk.Read(Path.Combine(f.DirectoryPath, "result.json"), ProtocolJson.Default.Result)!;
            Assert.Equal(exitEarly ? "healthCheckFailure" : "success", result.Outcome);
            if (!exitEarly)
                Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(f.Request.HealthTimeoutSeconds));
        }
        finally { Cleanup(host); Cleanup(application); }
    }

    [Fact]
    public async Task ReusedPidIdentityIsNotStopped()
    {
        using var application = Peer(30_000);
        try
        {
            var incorrect = Identity(application) with { StartTimeUtc = DateTimeOffset.UtcNow.AddDays(-1) };
            var processes = new Processes();
            Assert.False(processes.IsAlive(incorrect));
            await processes.StopAsync(incorrect, CancellationToken.None);
            Assert.False(application.HasExited);
            await processes.StopAsync(Identity(application), CancellationToken.None);
            Assert.True(application.HasExited);
        }
        finally { Cleanup(application); }
    }
}
