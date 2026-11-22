using System.Text.Json;
using System.Diagnostics;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class CrashCollectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("CON.txt")]
    [InlineData("lpt1")]
    [InlineData("com\u00b9.txt")]
    [InlineData("version.")]
    [InlineData("version ")]
    [InlineData("version\u007f")]
    public void UnsafeVersionsAreRejected(string? version) =>
        Assert.False(CrashCollection.SafeVersion(version));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("release-candidate")]
    public void SafeVersionsAreAccepted(string version) =>
        Assert.True(CrashCollection.SafeVersion(version));

    [Fact]
    public async Task UnsafeVersionIsRejectedBeforeReady()
    {
        using var f = new Fixture();
        f.Request = f.Request with { CrashCollectionEnabled = true, TargetVersion = "../bad" };
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
    }

    [Fact]
    public async Task DisabledCollectionDoesNotCreateEvidenceOrStatus()
    {
        using var f = new Fixture();
        f.Producer("completed");
        Assert.Equal("success", (await f.Run()).Outcome);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "crash-outbox")));
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "capture-status.json")));
    }

    [Fact]
    public async Task CaptureFailureDoesNotChangeSuccessfulUpdate()
    {
        using var f = new Fixture();
        f.Request = f.Request with
        {
            CrashCollectionEnabled = true,
            TargetVersion = "1.2.3",
            Updater = new ProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow)
        };
        f.Producer("completed");
        Assert.Equal("success", (await f.Run()).Outcome);
        var status = Disk.Read(Path.Combine(f.DirectoryPath, "capture-status.json"),
            ProtocolJson.Default.CaptureStatus)!;
        Assert.Single(status.Targets);
        Assert.Contains(status.Targets[0].State, new[] { "unavailable", "interrupted" });
        Assert.Equal("acknowledged", f.Outbox.State);
    }

    [Fact]
    public async Task RollbackPreservesPublishedEvidenceOutsideInstallation()
    {
        using var f = new Fixture();
        f.Request = f.Request with
        {
            AutoRollback = true,
            CrashCollectionEnabled = true,
            TargetVersion = "1.2.3",
            Updater = new ProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow)
        };
        f.Snapshot();
        f.Processes.Alive = false;
        var staging = Path.Combine(f.Root, "crash-outbox", "1.2.3", ".partial");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "crash.json"), "{}");
        File.WriteAllBytes(Path.Combine(staging, "crash.dmp"), [1]);
        var guid = Guid.NewGuid().ToString("D");
        CrashCollection.Publish(staging, guid, "app.exe", "1.2.3");
        f.Producer("failed", error: new Failure { Category = "updateFailure" });

        var result = await f.Run();

        Assert.Equal("updateFailure", result.Outcome);
        Assert.Equal("succeeded", result.Rollback);
        Assert.True(File.Exists(Path.Combine(f.Root, "crash-outbox", "1.2.3", guid, "crash.dmp")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
    }

    [Fact]
    public void CompleteCasePublishesSchemaTwoInOneDirectoryMove()
    {
        using var f = new Fixture();
        var versionRoot = Path.Combine(f.Root, "crash-outbox", "1.2.3");
        var staging = Path.Combine(versionRoot, ".partial");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "crash.json"), "{}");
        File.WriteAllBytes(Path.Combine(staging, "crash.dmp"), [1, 2, 3]);
        var guid = Guid.NewGuid().ToString("D");

        CrashCollection.Publish(staging, guid, "app.exe", "1.2.3");

        Assert.False(Directory.Exists(staging));
        var destination = Path.Combine(versionRoot, guid);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("schema").GetInt32());
        Assert.Equal(guid, root.GetProperty("guid").GetString());
        Assert.Equal("crash", root.GetProperty("kind").GetString());
        Assert.Equal("app.exe", root.GetProperty("targetName").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("pending", root.GetProperty("state").GetString());
        Assert.Equal(0, root.GetProperty("attempts").GetInt32());
        Assert.Equal(new[] { "manifest.json", "crash.json", "crash.dmp" },
            root.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public void IncompleteCaseNeverBecomesVisible()
    {
        using var f = new Fixture();
        var staging = Path.Combine(f.Root, "crash-outbox", "1.2.3", ".partial");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "crash.json"), "{}");
        File.WriteAllBytes(Path.Combine(staging, "crash.dmp"), []);
        var guid = Guid.NewGuid().ToString("D");
        Assert.Throws<InvalidDataException>(() => CrashCollection.Publish(staging, guid, "app.exe", "1.2.3"));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(staging)!, guid)));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void ProcdumpSuccessMeansOneCompleteDump()
    {
        using var f = new Fixture();
        var dump = Path.Combine(f.Root, "capture.dmp");
        Assert.False(CrashCollection.DumpSucceeded(1, dump));
        File.WriteAllBytes(dump, []);
        Assert.False(CrashCollection.DumpSucceeded(1, dump));
        File.WriteAllBytes(dump, [1]);
        Assert.True(CrashCollection.DumpSucceeded(1, dump));
        Assert.False(CrashCollection.DumpSucceeded(0, dump));
    }

    [Fact]
    public async Task CaptureJobStopsItsOwnedProcessOnClose()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell",
                "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            using (var job = CaptureJob.Create())
                job.Assign(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }
}
