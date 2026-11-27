using System.Diagnostics;
using System.Text.Json;
using GeneralUpdate.Bowl;
using GeneralUpdate.Bowl.ConsoleApp;

namespace Bowl.Console.Tests;

public sealed class OutboxConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bowl-outbox-" + Guid.NewGuid().ToString("N"));
    private OutboxManager Manager() => new(new WatchOptionsBuilder().WithInstallPath(_root).WithVersion("1").Build());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static Process StartPeer(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Bowl.slnx"))) root = root.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(root!.FullName, "tests", "Bowl.TestPeer", "bin", configuration, "net8.0", "Bowl.TestPeer.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("lock");
        start.ArgumentList.Add(path);
        return Process.Start(start)!;
    }

    [Fact]
    public async Task CaseLock_ExcludesOtherProcessAndRecoversAfterOwnerDies()
    {
        var manager = Manager();
        using var session = manager.CreateSession()!;
        var id = Path.GetFileName(session.Directory);
        var path = Path.Combine(manager.FailRoot, ".locks", id[..2] + ".lock");
        using (var blocked = StartPeer(path))
        {
            Assert.Equal("busy", await blocked.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, blocked.ExitCode);
        }
        session.Dispose();

        using var holder = StartPeer(path);
        Assert.Equal("acquired", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        holder.Kill();
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        FileStream? recovered = null;
        for (var i = 0; i < 100 && recovered == null; i++)
        {
            try { recovered = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50); }
        }
        using (recovered)
            Assert.NotNull(recovered);
        Assert.True(File.Exists(path));
        Assert.True(Directory.Exists(session.Directory));
    }

    [Fact]
    public async Task HookFinishesBeforeManifestAndCaseLockIsReleased()
    {
        var manager = Manager();
        using var session = manager.CreateSession()!;
        var scriptDirectory = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(scriptDirectory);
        var windows = OperatingSystem.IsWindows();
        File.WriteAllText(Path.Combine(scriptDirectory, windows ? "on-crash.cmd" : "on-crash.sh"), windows
            ? "@echo off\r\necho started>\"%BOWL_EVIDENCE_DIRECTORY%\\started\"\r\nping -n 3 127.0.0.1 >nul\r\necho evidence>\"%BOWL_EVIDENCE_DIRECTORY%\\hook.txt\"\r\n"
            : "echo started > \"$BOWL_EVIDENCE_DIRECTORY/started\"\nsleep 2\necho evidence > \"$BOWL_EVIDENCE_DIRECTORY/hook.txt\"\n");
        var loop = new SupervisorLoop(new WatchOptionsBuilder().WithInstallPath(_root).WithVersion("1").Build(), manager, new ExitCodeWatcher());
        var publication = Task.Run(() => loop.PublishEvidence(session.Directory, new WatchOutcome(-1), "enabled", scriptDirectory));
        var started = Path.Combine(session.Directory, "started");
        for (var i = 0; i < 100 && !File.Exists(started); i++) await Task.Delay(50);
        Assert.True(File.Exists(started));
        Assert.False(File.Exists(Path.Combine(session.Directory, "manifest.json")));
        var lockPath = Path.Combine(manager.FailRoot, ".locks", Path.GetFileName(session.Directory)[..2] + ".lock");
        using (var blocked = StartPeer(lockPath))
        {
            Assert.Equal("busy", await blocked.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.True(await publication.WaitAsync(TimeSpan.FromSeconds(15)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.Directory, "manifest.json")));
        Assert.Contains(manifest.RootElement.GetProperty("files").EnumerateArray(), f => f.GetString() == "hook.txt");
    }

    [Fact]
    public async Task FullOutboxStillMonitorsTargetWithoutWritingEvidence()
    {
        var manager = Manager();
        for (var i = 0; i < OutboxManager.MaxCasesPerVersion; i++)
            Directory.CreateDirectory(Path.Combine(manager.VersionRoot, Guid.NewGuid().ToString("D")));
        Assert.Null(manager.CreateSession());
        var start = new ProcessStartInfo("dotnet");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Bowl.slnx"))) root = root.Parent;
        start.ArgumentList.Add(Path.Combine(root!.FullName, "tests", "Bowl.TestPeer", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0", "Bowl.TestPeer.dll"));
        start.ArgumentList.Add("200");
        using var target = Process.Start(start)!;
        var loop = new SupervisorLoop(new WatchOptionsBuilder().WithInstallPath(_root).WithVersion("1").Build(), manager, new ThrowingWatcher());
        var result = await loop.SuperviseSessionAsync(target, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.Outcome.TargetExitCode);
        Assert.False(result.Crash);
        Assert.Equal(OutboxManager.MaxCasesPerVersion, Directory.GetDirectories(manager.VersionRoot).Length);
    }

    [Fact]
    public async Task ConcurrentAdmissionCannotExceedLimit()
    {
        var manager = Manager();
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            start.Wait();
            using var lease = manager.CreateSession();
        })).ToArray();
        start.Set();
        await Task.WhenAll(attempts);
        Assert.InRange(Directory.GetDirectories(manager.VersionRoot).Length, 1, OutboxManager.MaxCasesPerVersion);
    }

    [Fact]
    public void GlobalCapacityCountsLegacyAndIncompleteCases()
    {
        var manager = Manager();
        for (var v = 0; v < 10; v++)
            for (var i = 0; i < 10; i++)
                Directory.CreateDirectory(Path.Combine(manager.FailRoot, $"legacy-{v}", $"unknown-{i}"));
        Assert.Null(manager.CreateSession());
        Assert.Equal(100, Directory.EnumerateDirectories(manager.FailRoot)
            .Where(d => Path.GetFileName(d) != ".locks").Sum(d => Directory.GetDirectories(d).Length));
    }

    [Fact]
    public void FailedHookPreservesUnpublishedCase()
    {
        var manager = Manager();
        using var session = manager.CreateSession()!;
        File.WriteAllText(Path.Combine(session.Directory, "evidence.txt"), "keep");
        var loop = new SupervisorLoop(new WatchOptionsBuilder().WithInstallPath(_root).WithVersion("1").Build(), manager, new ExitCodeWatcher());
        Assert.False(loop.PublishEvidence(session.Directory, new WatchOutcome(-1), "enabled", _root));
        manager.CollectGarbage();
        Assert.False(File.Exists(Path.Combine(session.Directory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(session.Directory, "evidence.txt")));
    }

    [Fact]
    public void FailedManifestWriteCanBeRetriedWithoutLosingEvidence()
    {
        var manager = Manager();
        using var session = manager.CreateSession()!;
        File.WriteAllText(Path.Combine(session.Directory, "evidence.txt"), "keep");
        var temporary = Path.Combine(session.Directory, "manifest.json.tmp");
        Directory.CreateDirectory(temporary);
        var loop = new SupervisorLoop(new WatchOptionsBuilder().WithInstallPath(_root).WithVersion("1").Build(), manager, new ExitCodeWatcher());
        Assert.False(loop.PublishEvidence(session.Directory, new WatchOutcome(-1), null, _root));
        Assert.False(File.Exists(Path.Combine(session.Directory, "manifest.json")));
        Directory.Delete(temporary);
        Assert.True(loop.PublishEvidence(session.Directory, new WatchOutcome(-1), null, _root));
        Assert.True(File.Exists(Path.Combine(session.Directory, "evidence.txt")));
    }

    private sealed class ThrowingWatcher : IProcessWatcher
    {
        public string Name => "unexpected";
        public Task<WatchOutcome> WaitAsync(Process target, BowlContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The dump watcher must not run when capacity is exhausted.");
    }
}
