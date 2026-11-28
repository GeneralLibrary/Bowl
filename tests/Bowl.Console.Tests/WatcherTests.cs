using System.Diagnostics;
using GeneralUpdate.Bowl;
using GeneralUpdate.Bowl.ConsoleApp;
using GeneralUpdate.Bowl.Strategies;

namespace Bowl.Console.Tests;

[CollectionDefinition("Hook environment", DisableParallelization = true)]
public class HookEnvironmentCollection { }

[Collection("Hook environment")]
public class WatcherTests
{
    [Fact]
    public async Task ExitCodeWatcher_ReportsChildExitAndEmptyOutput()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 7")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 7\"");
        using var target = Process.Start(startInfo)!;
        var outcome = await ExitCodeWatcher.WaitTargetAsync(target, CancellationToken.None);
        Assert.Equal(7, outcome.TargetExitCode);
        Assert.Empty(outcome.OutputLines);
    }

    [Fact]
    public async Task ProcdumpWatcher_PreservesDumpWhenExternalUnixExitCodeIsUnavailable()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"bowl-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var startInfo = new ProcessStartInfo("/bin/sh", "-c \"sleep 1 & echo $!; wait\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var parent = Process.Start(startInfo)!;
        var pid = int.Parse((await parent.StandardOutput.ReadLineAsync())!);
        using var target = Process.GetProcessById(pid);
        try
        {
            File.WriteAllText(Path.Combine(root, "dump.dmp"), "dump");
            var context = new BowlContext { FailDirectory = root, DumpFileName = "dump.dmp" };
            var outcome = await new ProcdumpWatcher(new FakeToolStrategy())
                .WaitAsync(target, context, CancellationToken.None);
            Assert.Null(outcome.TargetExitCode);
            Assert.Equal(Path.Combine(root, "dump.dmp"), outcome.DumpPath);
            Assert.NotNull(outcome.OutputLines);
        }
        finally
        {
            await parent.WaitForExitAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeToolStrategy : IBowlStrategy
    {
        public ProcessStartInfo Prepare(in BowlContext context) => new("/bin/sh", "-c \"sleep 2\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        public Task PostProcessAsync(in BowlContext context, ProcessExitResult exitResult, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task Hooks_UseIndependentEnvironmentsWithoutChangingParentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bowl hooks & {Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var windows = OperatingSystem.IsWindows();
        File.WriteAllText(Path.Combine(root, windows ? "on-crash.cmd" : "on-crash.sh"),
            windows
                ? "@echo off\r\nsetlocal EnableDelayedExpansion\r\necho !BOWL_EVIDENCE_DIRECTORY!>\"!BOWL_EVIDENCE_DIRECTORY!\\result.txt\"\r\n"
                : "printf '%s\\n' \"$BOWL_EVIDENCE_DIRECTORY\" > \"$BOWL_EVIDENCE_DIRECTORY/result.txt\"\n");
        var current = Environment.CurrentDirectory;
        var originalSearchSetting = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
        try
        {
            if (windows) Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1");
            await Task.WhenAll(
                Task.Run(() => SupervisorLoop.RunOnCrashHook(first, "enabled", root)),
                Task.Run(() => SupervisorLoop.RunOnCrashHook(second, "enabled", root)));
            Assert.Equal(first, File.ReadAllText(Path.Combine(first, "result.txt")).Trim());
            Assert.Equal(second, File.ReadAllText(Path.Combine(second, "result.txt")).Trim());
            Assert.Equal(current, Environment.CurrentDirectory);
            Assert.False(File.Exists(Path.Combine(root, "on-crash.pointer")));
        }
        finally
        {
            if (windows) Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", originalSearchSetting);
            Directory.Delete(root, recursive: true);
        }
    }
}
