using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class TestSupervisorProcessTests
{
    [Fact]
    public async Task FailedWorkerProducesDurablePendingOutboxWithDiagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", Guid.NewGuid().ToString("D"));
        var variable = "BOWL_TEST_KEY_" + Guid.NewGuid().ToString("N");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            foreach (var dir in new[] { "install", "backup", "stage", "state" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            File.WriteAllText(Path.Combine(root, "install", "old.txt"), "old");
            File.WriteAllText(Path.Combine(root, "stage", "old.txt"), "new");
            File.WriteAllText(Path.Combine(root, "stage", "new.txt"), "added");
            File.WriteAllText(Path.Combine(root, "state", "test-report.json"), JsonSerializer.Serialize(new
            {
                url = $"http://127.0.0.1:{port}/", recordId = 42, type = 1,
                appKeyEnvironmentVariable = variable
            }));
            Environment.SetEnvironmentVariable(variable, "isolated-secret");
            using var supervisor = Start(root, "fail-after-first");
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(20));
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                using var document = JsonDocument.Parse(body);
                Assert.Equal("isolated-secret", document.RootElement.GetProperty("appKey").GetString());
                Assert.True(document.RootElement.GetProperty("diagnostics").GetProperty("rollback").GetBoolean());
                context.Response.StatusCode = 503;
                context.Response.Close();
                await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, supervisor.ExitCode);
                var reporting = Path.Combine(root, "state", "reporting");
                var outbox = Disk.Read(Path.Combine(reporting, "outbox.json"), ProtocolJson.Default.Outbox)!;
                Assert.Equal("pending", outbox.State);
                Assert.Equal("HTTP 503", outbox.LastError);
                Assert.Null(outbox.Payload.AppKey);
                Assert.DoesNotContain("isolated-secret", File.ReadAllText(Path.Combine(reporting, "request.json")));
                Assert.DoesNotContain("isolated-secret", File.ReadAllText(Path.Combine(reporting, "outbox.json")));
                Disk.Write(Path.Combine(reporting, "outbox.json"),
                    outbox with { NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, ProtocolJson.Default.Outbox);
                using var retry = StartCommand("test-report-retry", root);
                try
                {
                    var retryContext = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    using var retryReader = new StreamReader(retryContext.Request.InputStream, Encoding.UTF8);
                    var retryBody = await retryReader.ReadToEndAsync();
                    Assert.Equal(body, retryBody);
                    Assert.Equal(context.Request.Headers["Idempotency-Key"], retryContext.Request.Headers["Idempotency-Key"]);
                    var response = Encoding.UTF8.GetBytes("{\"code\":200,\"body\":true,\"isSuccess\":true}");
                    retryContext.Response.StatusCode = 200;
                    await retryContext.Response.OutputStream.WriteAsync(response);
                    retryContext.Response.Close();
                    await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(0, retry.ExitCode);
                    Assert.Equal("acknowledged", Disk.Read(Path.Combine(reporting, "outbox.json"),
                        ProtocolJson.Default.Outbox)!.State);
                }
                finally { if (!retry.HasExited) { retry.Kill(); await retry.WaitForExitAsync(); } }
            }
            finally { if (!supervisor.HasExited) { supervisor.Kill(); await supervisor.WaitForExitAsync(); } }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PinnedDirectoriesPermitSupervisedApplyAndRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", Guid.NewGuid().ToString("D"));
        try
        {
            foreach (var dir in new[] { "install", "backup", "stage", "state" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetOwner(identity.User!);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Path.Combine(root, "install")).SetAccessControl(security);
            File.WriteAllText(Path.Combine(root, "install", "old.txt"), "old");
            Fixture.RestrictFile(Path.Combine(root, "install", "old.txt"), identity.User!);
            File.WriteAllText(Path.Combine(root, "stage", "old.txt"), "new");
            File.WriteAllText(Path.Combine(root, "stage", "new.txt"), "added");
            // An elevated process creates objects owned by BUILTIN\Administrators,
            // so pin the whole staged layout back to the current user identity.
            foreach (var entry in new[] { "install", "backup", "stage", "state" })
                TrustedOwner.Apply(Path.Combine(root, entry), identity.User!);
            foreach (var entry in new[] { Path.Combine("install", "old.txt"), Path.Combine("stage", "old.txt"), Path.Combine("stage", "new.txt") })
                TrustedOwner.Apply(Path.Combine(root, entry), identity.User!);
            using var supervisor = Start(root, "fail-after-second", true);
            try
            {
                await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var output = await supervisor.StandardOutput.ReadToEndAsync();
                var error = await supervisor.StandardError.ReadToEndAsync();
                Assert.True(supervisor.ExitCode == 0, $"Exit {supervisor.ExitCode}: {output} {error}");
                Assert.Contains("TEST_RESULT workerFailed succeeded", output);
                Assert.Equal("old", File.ReadAllText(Path.Combine(root, "install", "old.txt")));
                Assert.False(File.Exists(Path.Combine(root, "install", "new.txt")));
            }
            finally { if (!supervisor.HasExited) { supervisor.Kill(); await supervisor.WaitForExitAsync(); } }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecoveryConflictReportsFailureAndRetainsOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", Guid.NewGuid().ToString("D"));
        try
        {
            foreach (var dir in new[] { "install", "backup", "stage", "state" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            File.WriteAllText(Path.Combine(root, "install", "old.txt"), "old");
            File.WriteAllText(Path.Combine(root, "stage", "old.txt"), "new");
            File.WriteAllText(Path.Combine(root, "stage", "new.txt"), "added");
            using var supervisor = Start(root, "conflict");
            await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, supervisor.ExitCode);
            Assert.Contains("TEST_RESULT workerFailed failed", await supervisor.StandardOutput.ReadToEndAsync());
            Assert.Equal("user edit", File.ReadAllText(Path.Combine(root, "install", "old.txt")));
            Assert.Contains("\"rollback\":\"failed\"", File.ReadAllText(Path.Combine(root, "state", "test-supervisor-result.json")));
            using var retry = Start(root, "success");
            await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, retry.ExitCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HostDeathTerminatesWorkerAndRestartRefusesUnprovenHandoff()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", Guid.NewGuid().ToString("D"));
        try
        {
            foreach (var dir in new[] { "install", "backup", "stage", "state" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            File.WriteAllText(Path.Combine(root, "install", "old.txt"), "old");
            File.WriteAllText(Path.Combine(root, "stage", "old.txt"), "new");
            File.WriteAllText(Path.Combine(root, "stage", "new.txt"), "added");
            using var supervisor = Start(root, "hold-after-first");
            try
            {
                var line = await supervisor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.StartsWith("TEST_WORKER ", line);
                using var worker = Process.GetProcessById(int.Parse(line!["TEST_WORKER ".Length..]));
                _ = worker.SafeHandle;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(Path.Combine(root, "first-write-complete")) && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                Assert.True(File.Exists(Path.Combine(root, "first-write-complete")));
                supervisor.Kill();
                await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var retry = Start(root, "success");
                await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(2, retry.ExitCode);
                Assert.Equal("new", File.ReadAllText(Path.Combine(root, "install", "old.txt")));
            }
            finally { if (!supervisor.HasExited) { supervisor.Kill(); await supervisor.WaitForExitAsync(); } }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("success", "new", true, "success", "notRequested")]
    [InlineData("fail-after-first", "old", false, "workerFailed", "succeeded")]
    [InlineData("fail-after-second", "old", false, "workerFailed", "succeeded")]
    public async Task IsolatedSupervisorStartsAuthenticatedWorkerAndRecovers(
        string mode, string expected, bool newExists, string outcome, string rollback)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", Guid.NewGuid().ToString("D"));
        try
        {
            foreach (var dir in new[] { "install", "backup", "stage", "state" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            File.WriteAllText(Path.Combine(root, "install", "old.txt"), "old");
            File.WriteAllText(Path.Combine(root, "install", "unknown.txt"), "user data");
            File.WriteAllText(Path.Combine(root, "stage", "old.txt"), "new");
            File.WriteAllText(Path.Combine(root, "stage", "new.txt"), "added");
            using var process = Start(root, mode);
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}: {output} {error}");
                Assert.Contains($"TEST_RESULT {outcome} {rollback}", output);
                Assert.Equal(expected, File.ReadAllText(Path.Combine(root, "install", "old.txt")));
                Assert.Equal(newExists, File.Exists(Path.Combine(root, "install", "new.txt")));
                Assert.Equal("user data", File.ReadAllText(Path.Combine(root, "install", "unknown.txt")));
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Process Start(string root, string mode, bool holdInstall = false)
    {
        var peer = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Bowl.TestPeer", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.TestPeer.exe"));
        var start = new ProcessStartInfo(peer)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "test-supervise", root, mode }) start.ArgumentList.Add(arg);
        if (holdInstall)
        {
            start.ArgumentList.Add(peer);
            start.ArgumentList.Add("--hold-install");
        }
        return Process.Start(start)!;
    }

    private static Process StartCommand(string command, string root)
    {
        var peer = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Bowl.TestPeer", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.TestPeer.exe"));
        var start = new ProcessStartInfo(peer)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(root);
        return Process.Start(start)!;
    }
}
