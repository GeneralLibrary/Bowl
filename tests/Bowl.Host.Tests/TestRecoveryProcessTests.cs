using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class TestRecoveryProcessTests
{
    [Fact]
    public async Task KilledRecoveryResumesWithoutLosingUnknownFiles()
    {
        using var tree = new TestTree();
        tree.Prepare();
        using var process = Start(tree, "--pause", "0", "applied");
        try
        {
            Assert.Equal("CHECKPOINT 0 applied",
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (!process.HasExited) process.Kill(); }
        Assert.Equal("old", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
        Assert.Equal("inProgress", ControlledJournal.Read(tree.Journal, tree.Attempt).Entries[0].State);
        using var resumed = Start(tree);
        await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, resumed.ExitCode);
        Assert.Contains("RECOVERY_COMPLETE", await resumed.StandardOutput.ReadToEndAsync());
        Assert.Equal("old", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
        Assert.False(File.Exists(Path.Combine(tree.Install, "new.bin")));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(tree.Install, "unknown.txt")));
        using var repeated = Start(tree);
        await repeated.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, repeated.ExitCode);
    }

    [Fact]
    public async Task KilledAfterNewFileDeletionResumes()
    {
        using var tree = new TestTree();
        tree.Prepare();
        using var first = Start(tree, "--pause", "1", "applied");
        try
        {
            Assert.Equal("CHECKPOINT 1 applied",
                await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            first.Kill();
            await first.WaitForExitAsync();
        }
        finally { if (!first.HasExited) first.Kill(); }
        Assert.False(File.Exists(Path.Combine(tree.Install, "new.bin")));
        using var resumed = Start(tree);
        await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, resumed.ExitCode);
        Assert.Equal("old", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(tree.Install, "unknown.txt")));
    }

    [Fact]
    public async Task ChangedPlanOrUserContentFailsClosed()
    {
        using var tree = new TestTree();
        tree.Prepare();
        using (var first = Start(tree, "--pause", "0", "intent"))
        {
            try
            {
                Assert.Equal("CHECKPOINT 0 intent",
                    await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                first.Kill();
                await first.WaitForExitAsync();
            }
            finally { if (!first.HasExited) first.Kill(); }
        }
        var plan = File.ReadAllText(tree.Plan);
        File.WriteAllText(tree.Plan, plan.Replace(Hash("new"), Hash("other")));
        using (var changed = Start(tree))
        {
            await changed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, changed.ExitCode);
            Assert.Contains("Immutable plan binding mismatch", await changed.StandardError.ReadToEndAsync());
        }
        File.WriteAllText(tree.Plan, plan);
        File.WriteAllText(Path.Combine(tree.Install, "app.bin"), "user edit");
        using var conflict = Start(tree);
        await conflict.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, conflict.ExitCode);
        Assert.Equal("user edit", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(tree.Install, "new.bin")));
    }

    [Fact]
    public async Task ConcurrentRecoveryIsRejectedWhileFirstProcessOwnsLock()
    {
        using var tree = new TestTree();
        tree.Prepare();
        using var first = Start(tree, "--pause", "0", "intent");
        try
        {
            Assert.Equal("CHECKPOINT 0 intent",
                await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            using var second = Start(tree);
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, second.ExitCode);
            Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
        }
        finally
        {
            if (!first.HasExited) { first.Kill(); await first.WaitForExitAsync(); }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    using var released = new FileStream(Path.Combine(tree.Root, "state", "recovery.lock"),
                        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
            }
        }
    }

    [Theory]
    [InlineData("path", "../escape")]
    [InlineData("snapshotPath", "C:\\outside\\app.bin")]
    [InlineData("targetPath", "C:\\outside\\app.bin")]
    public async Task UnsafePlanPathsFailBeforeJournal(string property, string value)
    {
        using var tree = new TestTree();
        tree.Prepare();
        var plan = JsonNode.Parse(File.ReadAllText(tree.Plan))!;
        plan["entries"]![0]![property] = value;
        File.WriteAllText(tree.Plan, plan.ToJsonString());
        using var process = Start(tree);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, process.ExitCode);
        Assert.False(File.Exists(tree.Journal));
        Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
    }

    [Fact]
    public async Task ProtectedPathOverlapFailsBeforeJournal()
    {
        using var tree = new TestTree();
        tree.Prepare();
        var plan = JsonNode.Parse(File.ReadAllText(tree.Plan))!;
        plan["protectedPaths"]!.AsArray().Add("app.bin\\nested");
        File.WriteAllText(tree.Plan, plan.ToJsonString());
        using var process = Start(tree);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, process.ExitCode);
        Assert.False(File.Exists(tree.Journal));
        Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1,\"targetRoot\":\"x\",\"stateDirectory\":\"x\",\"protectedPaths\":[],\"entries\":[]}")]
    [InlineData("{\"version\":\"1\",\"targetRoot\":\"x\",\"stateDirectory\":\"x\",\"protectedPaths\":[],\"entries\":[]}")]
    public async Task MalformedPlansFailWithoutJournalOrMutation(string plan)
    {
        using var tree = new TestTree();
        tree.Prepare();
        File.WriteAllText(tree.Plan, plan);
        using var process = Start(tree);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, process.ExitCode);
        Assert.False(File.Exists(tree.Journal));
        Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "app.bin")));
    }

    private static Process Start(TestTree tree, params string[] extra)
    {
        var peer = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Bowl.TestPeer", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.TestPeer.dll"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { peer, "test-recover", tree.Root, tree.Attempt, tree.Plan }.Concat(extra))
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed class TestTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", Guid.NewGuid().ToString("D"));
        public string Attempt => Path.GetFileName(Root);
        public string Install => Path.Combine(Root, "install");
        public string Backup => Path.Combine(Root, "backup");
        public string Plan => Path.Combine(Root, "state", "apply-0123456789abcdef0123456789abcdef.json");
        public string Journal => Path.Combine(Root, "state", "recovery-journal.json");

        public void Prepare()
        {
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Backup);
            Directory.CreateDirectory(Path.Combine(Root, "state"));
            Directory.CreateDirectory(Path.Combine(Root, "stage"));
            File.WriteAllText(Path.Combine(Install, "app.bin"), "new");
            File.WriteAllText(Path.Combine(Install, "new.bin"), "added");
            File.WriteAllText(Path.Combine(Install, "unknown.txt"), "user data");
            File.WriteAllText(Path.Combine(Backup, "app.bin"), "old");
            File.WriteAllText(Plan, JsonSerializer.Serialize(new
            {
                version = 1, targetRoot = Install, stateDirectory = Path.Combine(Root, "state"),
                protectedPaths = new[] { ".backups" }, entries = new object[]
                {
                    new { path = "app.bin", targetPath = Path.Combine(Install, "app.bin"),
                        stagedPath = Path.Combine(Root, "stage", "app.bin"), updatedSha256 = Hash("new"),
                        snapshotPath = Path.Combine(Backup, "app.bin"), originalSha256 = Hash("old"),
                        temporaryPath = Path.Combine(Install, ".gu-apply-0123456789abcdef0123456789abcdef") },
                    new { path = "new.bin", targetPath = Path.Combine(Install, "new.bin"),
                        stagedPath = Path.Combine(Root, "stage", "new.bin"), updatedSha256 = Hash("added"),
                        snapshotPath = (string?)null, originalSha256 = (string?)null,
                        temporaryPath = Path.Combine(Install, ".gu-apply-0123456789abcdef0123456789abcdee") }
                }
            }));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
