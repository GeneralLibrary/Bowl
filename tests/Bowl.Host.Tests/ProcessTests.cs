using System.Diagnostics;
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

    private static Process Start(string dll, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static Process Peer(int milliseconds) => Start(Path.Combine(Repository, "tests", "Bowl.TestPeer",
        "bin", Configuration, "net8.0", "Bowl.TestPeer.dll"), milliseconds.ToString());

    private static Process Host(Fixture f) => Start(Path.Combine(Repository, "src", "Bowl.Host",
        "bin", Configuration, "net8.0", "Bowl.Host.dll"),
        "--attempt", f.Request.AttemptId, "--state-root", f.Root);

    [Fact]
    public async Task RealHostCrashCanResumeObservationAndRollbackWithRetainedSnapshot()
    {
        using var f = new Fixture();
        using var updater = Peer(30_000);
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
                Assert.Equal("succeeded", result.Rollback);
                Assert.Equal("old", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
                Assert.False(File.Exists(Path.Combine(f.Request.InstallPath, "new-only.txt")));
                Assert.True(File.Exists(Path.Combine(f.Request.BackupDirectory!, "application.txt")));
            }
            finally { Cleanup(resumed); }
        }
        finally { Cleanup(first); Cleanup(updater); }
    }

    private static ProcessIdentity Identity(Process process) => new(process.Id, process.StartTime.ToUniversalTime());

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

    private static Task<string> ReadErrorIfExited(Process process) => process.HasExited
        ? process.StandardError.ReadToEndAsync() : Task.FromResult("Host still running");

    [Fact]
    public async Task RealHostReadyIsSamePidAndStartTime_ThenConfirmsFiles()
    {
        using var f = new Fixture();
        f.Request = f.Request with { Updater = ProcessIdentity.Current(), Report = new() };
        f.Save();
        using var host = Host(f);
        try
        {
            var ready = await Ready(f, host);
            Assert.Equal(Identity(host), ready.Host);
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
        using var f = new Fixture();
        using var updater = Peer(30_000);
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
        using var f = new Fixture();
        using var application = Peer(30_000);
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
