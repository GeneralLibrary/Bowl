using System.Diagnostics;
using System.Linq;
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

    private static Process Peer(int milliseconds, params string[] arguments) => Start(
        Path.Combine(Repository, "tests", "Bowl.TestPeer", "bin", Configuration, "net8.0", "Bowl.TestPeer.dll"),
        new[] { milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Concat(arguments).ToArray());

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

    private static async Task WaitForCaptureTools(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var tools = new[] { "procdump", "procdump64", "procdump64a" }
                .SelectMany(Process.GetProcessesByName).ToArray();
            try
            {
                if (tools.Length >= count)
                    return;
            }
            finally
            {
                foreach (var tool in tools)
                    tool.Dispose();
            }
            await Task.Delay(50, timeout.Token);
        }
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
        using var f = new Fixture();
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

    [Fact]
    public async Task RealCrashPublishesDumpAndReport()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        using var updater = Peer(10_000, "--crash");
        var updaterIdentity = Identity(updater);
        f.Request = f.Request with
        {
            Updater = updaterIdentity, CrashCollectionEnabled = true, TargetVersion = "1.2.3", Report = new()
        };
        f.Save();
        f.Producer("filesApplying");
        using var host = Host(f);
        try
        {
            await Ready(f, host);
            await Wait(updater);
            await Wait(host);

            var result = Disk.Read(Path.Combine(f.DirectoryPath, "result.json"), ProtocolJson.Default.Result)!;
            Assert.Equal("updaterTerminated", result.Outcome);
            var status = Disk.Read(Path.Combine(f.DirectoryPath, "capture-status.json"),
                ProtocolJson.Default.CaptureStatus)!;
            Assert.Equal("published", Assert.Single(status.Targets).State);
            var versionRoot = Path.Combine(f.Root, "crash-outbox", "1.2.3");
            var caseDirectory = Assert.Single(Directory.GetDirectories(versionRoot));
            Assert.True(new FileInfo(Path.Combine(caseDirectory, "crash.dmp")).Length > 0);
            Assert.True(new FileInfo(Path.Combine(caseDirectory, "crash.json")).Length > 0);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDirectory, "manifest.json")));
            Assert.Equal(new[] { "crash.json", "crash.dmp" },
                manifest.RootElement.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray());
        }
        finally { Cleanup(host); Cleanup(updater); }
    }

    [Fact]
    public async Task ProcessAliveSuccessCancelsCaptureWithoutStoppingApplication()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        using var updater = Peer(30_000);
        using var application = Peer(30_000);
        var applicationIdentity = Identity(application);
        f.Request = f.Request with
        {
            Updater = Identity(updater), LaunchMode = "processAlive", HealthTimeoutSeconds = 12,
            CrashCollectionEnabled = true, TargetVersion = "1.2.3", Report = new()
        };
        f.Save();
        f.Producer("awaitingHealth", applicationIdentity);
        using var host = Host(f);
        try
        {
            await Ready(f, host);
            await WaitForCaptureTools(2);
            await Wait(host);

            var result = Disk.Read(Path.Combine(f.DirectoryPath, "result.json"), ProtocolJson.Default.Result)!;
            Assert.Equal("success", result.Outcome);
            Assert.Equal("processAlive", result.Verification);
            Assert.False(application.HasExited);
            var status = Disk.Read(Path.Combine(f.DirectoryPath, "capture-status.json"),
                ProtocolJson.Default.CaptureStatus)!;
            Assert.Contains(status.Targets, target =>
                target.Identity == applicationIdentity && target.State == "interrupted");
        }
        finally { Cleanup(host); Cleanup(application); Cleanup(updater); }
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
