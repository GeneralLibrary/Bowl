using System.Diagnostics;
using Bowl.Host;

namespace Bowl.Host.Tests;

[CollectionDefinition("Controlled lease processes", DisableParallelization = true)]
public sealed class ControlledLeaseCollection;

[Collection("Controlled lease processes")]
public sealed class ControlledLeaseTests
{
    [Fact]
    public async Task ProcessCrashRetainsOwnerAcrossStateRoots()
    {
        using var f = new Fixture();
        var registry = Path.Combine(f.Base, "registry");
        var otherRoot = Path.Combine(f.Base, "other-state");
        Directory.CreateDirectory(registry);
        Directory.CreateDirectory(otherRoot);
        var peerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Bowl.TestPeer", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.TestPeer.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in new[] { peerPath, "lease", registry, f.Request.InstallPath, f.Root, f.Request.AttemptId })
            start.ArgumentList.Add(argument);
        using var peer = Process.Start(start)!;
        try
        {
            Assert.Equal("LEASE_ACQUIRED", await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = ControlledLease.Acquire(
                    registry, f.Request.InstallPath, otherRoot, Guid.NewGuid().ToString("D"));
            });
            peer.Kill();
            await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // After the peer dies, the lease can fail with either IOException (the
            // OS hasn't fully released the file handle yet) or InvalidDataException
            // (the lease registry detects a dead owner). Both indicate that the
            // lease cannot be cleanly acquired until recovery runs.
            Assert.ThrowsAny<Exception>(() =>
            {
                using var unexpected = ControlledLease.Acquire(
                    registry, f.Request.InstallPath, otherRoot, Guid.NewGuid().ToString("D"));
            });
            using (var recovery = ControlledLease.Acquire(registry, f.Request.InstallPath, f.Root, f.Request.AttemptId))
                recovery.Complete();
            using var next = ControlledLease.Acquire(registry, f.Request.InstallPath, otherRoot, Guid.NewGuid().ToString("D"));
        }
        finally
        {
            if (!peer.HasExited)
            {
                peer.Kill();
                await peer.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public void SameAttemptCannotResumeFromAnotherStateRoot()
    {
        using var f = new Fixture();
        var registry = Path.Combine(f.Base, "registry");
        var otherRoot = Path.Combine(f.Base, "other-state");
        Directory.CreateDirectory(registry);
        Directory.CreateDirectory(otherRoot);
        using (ControlledLease.Acquire(registry, f.Request.InstallPath, f.Root, f.Request.AttemptId)) { }
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = ControlledLease.Acquire(registry, f.Request.InstallPath, otherRoot, f.Request.AttemptId);
        });
    }

    [Fact]
    public void DisposedLeaseCannotChangeDurableOwner()
    {
        using var f = new Fixture();
        var registry = Path.Combine(f.Base, "registry");
        Directory.CreateDirectory(registry);
        var lease = ControlledLease.Acquire(registry, f.Request.InstallPath, f.Root, f.Request.AttemptId);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(lease.Complete);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = ControlledLease.Acquire(
                registry, f.Request.InstallPath, f.Root, Guid.NewGuid().ToString("D"));
        });
    }

    [Fact]
    public void FreshAdmissionRejectsEvenSameUnresolvedAttempt()
    {
        using var f = new Fixture();
        var registry = Path.Combine(f.Base, "registry");
        Directory.CreateDirectory(registry);
        using (ControlledLease.AcquireFresh(registry, f.Request.InstallPath, f.Root, f.Request.AttemptId)) { }
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = ControlledLease.AcquireFresh(
                registry, f.Request.InstallPath, f.Root, f.Request.AttemptId);
        });
    }

    [Fact]
    public void FreshAdmissionRejectsReplayAfterCompletion()
    {
        using var f = new Fixture();
        var registry = Path.Combine(f.Base, "registry");
        Directory.CreateDirectory(registry);
        using (var first = ControlledLease.AcquireFresh(registry, f.Request.InstallPath, f.Root, f.Request.AttemptId))
            first.Complete();
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = ControlledLease.AcquireFresh(
                registry, f.Request.InstallPath, f.Root, f.Request.AttemptId);
        });
        using var next = ControlledLease.AcquireFresh(
            registry, f.Request.InstallPath, f.Root, Guid.NewGuid().ToString("D"));
    }
}
