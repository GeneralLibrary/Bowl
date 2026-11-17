using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task UnknownVersionIsRejectedBeforeReady()
    {
        using var f = new Fixture();
        f.Request = f.Request with { ProtocolVersion = 2 };
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
    }

    [Fact]
    public void MissingVersionIsNotSilentlyDefaulted()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            """{"attemptId":"test","host":{"pid":1,"startTimeUtc":"2026-01-01T00:00:00Z"}}""",
            ProtocolJson.Default.Ready));
    }

    [Fact]
    public async Task OverlappingStateIsRejected()
    {
        using var f = new Fixture();
        f.Request = f.Request with { InstallPath = Path.Combine(f.Root, "install") };
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
    }

    [Theory]
    [InlineData("https://user:password@example.invalid/report")]
    [InlineData("https://example.invalid/report?token=secret")]
    [InlineData("https://example.invalid/report#secret")]
    [InlineData("file:///tmp/report")]
    public async Task EmbeddedCredentialUrlsAreRejected(string url)
    {
        using var f = new Fixture();
        f.Request = f.Request with { Report = f.Request.Report with { Url = url } };
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
    }

    [Fact]
    public async Task DuplicateHostCannotAcquireOwnership()
    {
        using var f = new Fixture();
        using var ownership = Disk.Lock(f.DirectoryPath);
        await Assert.ThrowsAsync<IOException>(f.Run);
    }

    [Fact]
    public void ReadersPermitAtomicReplace()
    {
        using var f = new Fixture();
        f.Save();
        var path = Path.Combine(f.DirectoryPath, "request.json");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Disk.Write(path, f.Request with { TargetVersion = "2" }, ProtocolJson.Default.Request);
        Assert.Equal("2", Disk.Read(path, ProtocolJson.Default.Request)!.TargetVersion);
    }

    [Fact]
    public async Task MalformedProducerDoesNotBecomeUpdateFailure()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.DirectoryPath, "producer.json"), "{invalid");
        Assert.Equal("monitorUnavailable", (await f.Run()).Outcome);
        Assert.Empty(f.Handler.Bodies);
    }

    [Fact]
    public async Task MismatchedProducerAttemptIsMonitoringUnavailable()
    {
        using var f = new Fixture();
        Disk.Write(Path.Combine(f.DirectoryPath, "producer.json"), new Producer
        {
            AttemptId = Guid.NewGuid().ToString("D"), Stage = "completed", UpdatedAtUtc = DateTimeOffset.UtcNow
        }, ProtocolJson.Default.Producer);
        Assert.Equal("monitorUnavailable", (await f.Run()).Outcome);
    }

    [Fact]
    public void OversizedRecordIsRejected()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.DirectoryPath, "producer.json");
        File.WriteAllBytes(path, new byte[Disk.MaximumRecordBytes + 1]);
        Assert.Throws<InvalidDataException>(() => Disk.Read(path, ProtocolJson.Default.Producer));
    }

    [Fact]
    public async Task CredentialIsRedactedFromLocalFailure_AndNeverSentOverHttp()
    {
        using var f = new Fixture();
        var variable = "BOWL_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "sensitive-test-value");
        try
        {
            f.Request = f.Request with { Report = f.Request.Report with { CredentialEnvironmentVariable = variable } };
            f.Producer("failed", error: new Failure
            {
                Category = "updateFailure", Message = "secret sensitive-test-value",
                StackTrace = "sensitive-test-value", FailedPath = "sensitive-test-value"
            });
            var result = await f.Run();
            Assert.DoesNotContain("sensitive-test-value",
                JsonSerializer.Serialize(result, ProtocolJson.Default.Result));
            Assert.Equal("pending", f.Outbox.State);
            Assert.Empty(f.Handler.Bodies);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task AccessDeniedDuringObserverProbeCannotPublishReady()
    {
        using var f = new Fixture();
        f.Processes.Probe = _ => throw new UnauthorizedAccessException("denied");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(f.Run);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
    }

    [Fact]
    public void EmptySnapshotDoesNotDeleteInstallation()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Request.BackupDirectory!);
        File.WriteAllText(Path.Combine(f.Request.InstallPath, "keep.txt"), "keep");
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(f.Request.BackupDirectory!, f.Request.InstallPath));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(f.Request.InstallPath, "keep.txt")));
    }

    [Fact]
    public void NestedSnapshotIsPreservedAndMirrorRemovesExtraDirectories()
    {
        using var f = new Fixture();
        f.Snapshot();
        Directory.CreateDirectory(Path.Combine(f.Request.InstallPath, "extra", "child"));
        File.WriteAllText(Path.Combine(f.Request.InstallPath, "extra", "child", "extra.txt"), "remove");
        Directory.CreateDirectory(Path.Combine(f.Request.BackupDirectory!, "nested"));
        File.WriteAllText(Path.Combine(f.Request.BackupDirectory!, "nested", "restore.txt"), "old");
        Recovery.Restore(f.Request.BackupDirectory!, f.Request.InstallPath);
        Assert.False(Directory.Exists(Path.Combine(f.Request.InstallPath, "extra")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(f.Request.InstallPath, "nested", "restore.txt")));
        Assert.True(File.Exists(Path.Combine(f.Request.BackupDirectory!, "nested", "restore.txt")));
    }

    [Fact]
    public async Task ExcessiveBackoffIsCappedAtOneHour()
    {
        using var f = new Fixture();
        f.Producer("completed");
        f.Handler.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        await f.Run();
        Disk.Write(Path.Combine(f.DirectoryPath, "outbox.json"), f.Outbox with
        {
            Attempts = 100, NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        }, ProtocolJson.Default.Outbox);
        var before = DateTimeOffset.UtcNow;
        await f.Run();
        Assert.InRange(f.Outbox.NextAttemptUtc - before, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.Equal(101, f.Outbox.Attempts);
    }

    [Fact]
    public async Task RetrySweepSkipsAcknowledgedAttemptsAndReconcilesMissingOutbox()
    {
        using var f = new Fixture();
        f.Request = f.Request with { Report = new() };
        f.Producer("completed");
        await f.Run();
        var firstOutbox = Path.Combine(f.DirectoryPath, "outbox.json");
        File.Delete(firstOutbox);
        Assert.Equal(0, await Program.Main(["--retry", "1", "--state-root", f.Root]));
        Assert.Equal("localOnly", f.Outbox.State);
        Assert.Equal(0, await Program.Main(["--retry", "1", "--state-root", f.Root]));
    }

    [Fact]
    public async Task AdmissionRejectsMoreThanTenThousandAttemptsWithoutReady()
    {
        using var f = new Fixture();
        var attempts = Path.Combine(f.Root, "attempts");
        for (var index = 0; index < 10_000; index++)
            Directory.CreateDirectory(Path.Combine(attempts, "history-" + index));
        f.Producer("completed");
        await Assert.ThrowsAsync<InvalidDataException>(f.Run);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "ready.json")));
    }

    [Fact]
    public void LinkedSnapshotIsRejectedBeforeDeletingAnyInstalledFiles()
    {
        using var f = new Fixture();
        f.Snapshot();
        var outside = Path.Combine(f.Base, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "safe");
        var link = Path.Combine(f.Request.BackupDirectory!, "linked");
        if (OperatingSystem.IsWindows())
            CreateJunction(link, outside); // native junction — no shell, no mklink process
        else
            Directory.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() => Recovery.Restore(f.Request.BackupDirectory!, f.Request.InstallPath));
            Assert.Equal("new", File.ReadAllText(Path.Combine(f.Request.InstallPath, "application.txt")));
            Assert.Equal("safe", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally { Directory.Delete(link); }
    }

    // ── Native junction creation ──────────────────────────────────────────

    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
        FileShare shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        byte[] inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    /// <summary>
    ///     Creates a directory junction via FSCTL_SET_REPARSE_POINT — the same reparse
    ///     point <c>mklink /J</c> produces, without spawning a shell in the test.
    /// </summary>
    private static void CreateJunction(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);
        using var handle = CreateFile(junctionPath, GENERIC_READ | GENERIC_WRITE,
            FileShare.None, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(targetPath));
        // Header: tag(4) + dataLength(2) + reserved(2); body: substitute offset(2) +
        // substitute length(2) + print offset(2) + print length(2); then the path,
        // followed by the empty print name's NUL plus 2 bytes of slack — the layout
        // mklink itself produces.
        var buffer = new byte[16 + substitute.Length + 4];
        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(substitute.Length + 12)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        substitute.CopyTo(buffer, 16);

        if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
