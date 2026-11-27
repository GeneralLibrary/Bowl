using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using System.Net;
using Bowl.Host;

// This isolated fixture is not a service, production authorization, or a recovery supervisor.
internal static class TestSupervisor
{
    public static async Task<int> Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("TEST_SUPERVISOR_FAILED: Windows required.");
            return 2;
        }
        try { return await RunWindows(args); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException or
                                   System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            Console.Error.WriteLine($"TEST_SUPERVISOR_FAILED: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindows(string[] args)
    {
        if (args.Length is < 3 or > 5 || args.Length == 5 && args[4] != "--hold-install" ||
            args[2] is not ("success" or "fail-after-first" or "fail-after-second" or "hold-after-first" or "conflict"))
            throw new InvalidDataException("Usage: test-supervise <root> <mode> [worker-exe].");
        var (root, attempt) = ValidateRoot(args[1]);
        var install = Path.Combine(root, "install");
        var state = Path.Combine(root, "state");
        var registry = Path.Combine(Path.GetTempPath(), "BowlControlledIntegration", "registry");
        Paths.NoLinks(registry);
        Directory.CreateDirectory(registry);
        using var lease = ControlledLease.Acquire(registry, install, state, attempt);
        using var identity = WindowsIdentity.GetCurrent();
        using var heldInstall = args.Length == 5
            ? ControlledTree.HoldMutationSubtree(install, new HashSet<SecurityIdentifier> { identity.User! })
            : null;
        var marker = Path.Combine(state, "test-supervisor-started");
        Paths.NoLinks(marker);
        using (var started = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            started.WriteByte(1);
            started.Flush(true);
        }
        var pipeName = "bowl-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = ProcessIdentity.Current();
        var exe = Paths.Absolute(args.Length >= 4 ? args[3] : Path.Combine(AppContext.BaseDirectory, "Bowl.TestPeer.exe"));
        using var worker = ControlledWorker.Start(exe,
            ["test-controlled-worker", pipeName, host.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
             host.StartTimeUtc.ToString("O"), root, attempt, args[2]], root);
        Console.WriteLine($"TEST_WORKER {worker.Identity.Pid}");
        Console.Out.Flush();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var authenticated = ControlledChannel.AuthenticateClient(pipe, worker.Identity);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        if (await reader.ReadLineAsync(timeout.Token) != "READY")
            throw new InvalidDataException("Unexpected worker handshake.");
        await writer.WriteLineAsync("GO");
        var response = await reader.ReadLineAsync(timeout.Token);
        await authenticated.WaitForExitAsync(timeout.Token);
        string outcome;
        string rollback;
        if (response == "DONE" && worker.ExitCode == 0)
        {
            outcome = "success";
            rollback = "notRequested";
        }
        else
        {
            outcome = "workerFailed";
            var plans = Directory.GetFiles(state, "apply-*.json");
            if (plans.Length != 1)
                throw new InvalidDataException("Exactly one fixture plan is required for recovery.");
            var result = await TestRecovery.Run(["test-recover", root, attempt, plans[0]]);
            rollback = result == 0 ? "succeeded" : "failed";
        }
        var resultPath = Path.Combine(state, "test-supervisor-result.json");
        Paths.NoLinks(resultPath);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { outcome, rollback }));
        await ReportIfConfigured(state, install, attempt, host, worker.Identity, outcome, rollback);
        if (rollback != "failed") lease.Complete();
        Console.WriteLine($"TEST_RESULT {outcome} {rollback}");
        return rollback == "failed" ? 2 : 0;
    }

    public static async Task<int> Worker(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        try { return await WorkerWindows(args); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException or
                                   System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            Console.Error.WriteLine($"TEST_WORKER_FAILED: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> WorkerWindows(string[] args)
    {
        if (args.Length != 7 || !int.TryParse(args[2], out var pid) ||
            !DateTimeOffset.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var start) ||
            args[6] is not ("success" or "fail-after-first" or "fail-after-second" or "hold-after-first" or "conflict"))
            throw new InvalidDataException("Invalid fixture worker arguments.");
        var (root, attempt) = ValidateRoot(args[4]);
        if (attempt != args[5]) throw new InvalidDataException("Attempt mismatch.");
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);
        using var host = ControlledChannel.AuthenticateServer(pipe, new ProcessIdentity(pid, start));
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("READY");
        if (await reader.ReadLineAsync(timeout.Token) != "GO")
            throw new InvalidDataException("No authorization.");
        var install = Path.Combine(root, "install");
        var backup = Path.Combine(root, "backup");
        var stage = Path.Combine(root, "stage");
        var state = Path.Combine(root, "state");
        var old = Path.Combine(install, "old.txt");
        var added = Path.Combine(install, "new.txt");
        var original = Hash(old);
        File.Copy(old, Path.Combine(backup, "old.txt"), false);
        var firstTemp = Path.Combine(install, ".gu-apply-" + Guid.NewGuid().ToString("N"));
        var secondTemp = Path.Combine(install, ".gu-apply-" + Guid.NewGuid().ToString("N"));
        var plan = new
        {
            version = 1, targetRoot = install, stateDirectory = state, protectedPaths = new[] { ".backups" },
            entries = new object[]
            {
                new { path = "old.txt", targetPath = old, stagedPath = Path.Combine(stage, "old.txt"),
                    updatedSha256 = Hash(Path.Combine(stage, "old.txt")), snapshotPath = Path.Combine(backup, "old.txt"),
                    originalSha256 = original, temporaryPath = firstTemp },
                new { path = "new.txt", targetPath = added, stagedPath = Path.Combine(stage, "new.txt"),
                    updatedSha256 = Hash(Path.Combine(stage, "new.txt")), snapshotPath = (string?)null,
                    originalSha256 = (string?)null, temporaryPath = secondTemp }
            }
        };
        var planPath = Path.Combine(state, "apply-" + Guid.NewGuid().ToString("N") + ".json");
        using (var file = new FileStream(planPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, plan);
            file.Flush(true);
        }
        File.Copy(Path.Combine(stage, "old.txt"), firstTemp);
        File.Replace(firstTemp, old, null);
        if (args[6] == "hold-after-first")
        {
            File.WriteAllText(Path.Combine(root, "first-write-complete"), "done");
            await Task.Delay(Timeout.Infinite, timeout.Token);
        }
        if (args[6] == "conflict")
        {
            File.WriteAllText(old, "user edit");
            return 19;
        }
        if (args[6] == "fail-after-first") return 19;
        File.Copy(Path.Combine(stage, "new.txt"), secondTemp);
        File.Move(secondTemp, added);
        if (args[6] == "fail-after-second") return 19;
        await writer.WriteLineAsync("DONE");
        return 0;
    }

    private static (string Root, string Attempt) ValidateRoot(string input)
    {
        var root = Paths.Absolute(input);
        var allowed = Paths.Absolute(Path.Combine(Path.GetTempPath(), "BowlControlledIntegration"));
        var attempt = Path.GetFileName(root);
        if (!Guid.TryParseExact(attempt, "D", out var id) || id.ToString("D") != attempt ||
            !string.Equals(Path.GetDirectoryName(root), allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid test root.");
        foreach (var name in new[] { "install", "backup", "stage", "state" })
            if (!Directory.Exists(Paths.Absolute(Path.Combine(root, name))))
                throw new InvalidDataException("Fixture directory missing.");
        return (root, attempt);
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static async Task ReportIfConfigured(string state, string install, string attempt,
        ProcessIdentity host, ProcessIdentity worker, string outcome, string rollback)
    {
        var configPath = Path.Combine(state, "test-report.json");
        Paths.NoLinks(configPath);
        if (!File.Exists(configPath)) return;
        using var configFile = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (configFile.Length > 16_384)
            throw new InvalidDataException("Test report configuration is too large.");
        var report = await JsonSerializer.DeserializeAsync<ReportConfiguration>(configFile,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("Missing test report configuration.");
        ValidateTestReport(report);
        var directory = Path.Combine(state, "reporting");
        Directory.CreateDirectory(directory);
        Paths.NoLinks(directory);
        using var reportLock = Disk.Lock(directory);
        var request = new Request
        {
            AttemptId = attempt, CreatedAtUtc = DateTimeOffset.UtcNow, Updater = worker,
            InstallPath = install, LaunchMode = "filesOnly", Report = report
        };
        var result = new Result
        {
            AttemptId = attempt, EventId = attempt + "-terminal-v1", Host = host, Updater = worker,
            Outcome = outcome == "success" ? "success" : "updateFailure", Stage = "filesApplying",
            Verification = "none", ObservedAtUtc = DateTimeOffset.UtcNow, Rollback = rollback,
            RollbackError = rollback == "failed" ? "InvalidDataException" : null
        };
        Disk.Write(Path.Combine(directory, "request.json"), request, ProtocolJson.Default.Request);
        Disk.Write(Path.Combine(directory, "result.json"), result, ProtocolJson.Default.Result);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler);
        await new Delivery(client).DeliverAsync(directory, request, result, CancellationToken.None);
    }

    public static async Task<int> RetryReport(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new InvalidDataException("Usage: test-report-retry <test-root>.");
            var (root, attempt) = ValidateRoot(args[1]);
            var directory = Paths.Absolute(Path.Combine(root, "state", "reporting"));
            using var reportLock = Disk.Lock(directory);
            var request = Disk.Read(Path.Combine(directory, "request.json"), ProtocolJson.Default.Request)
                ?? throw new InvalidDataException("Missing test report request.");
            var result = Disk.Read(Path.Combine(directory, "result.json"), ProtocolJson.Default.Result)
                ?? throw new InvalidDataException("Missing test report result.");
            Disk.Validate(request, attempt);
            Disk.Validate(result, attempt);
            if (result.EventId != attempt + "-terminal-v1" ||
                result.Outcome is not ("success" or "updateFailure") ||
                result.Rollback is not ("notRequested" or "succeeded" or "failed"))
                throw new InvalidDataException("Invalid test report result.");
            ValidateTestReport(request.Report);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler);
            await new Delivery(client).DeliverAsync(directory, request, result, CancellationToken.None);
            var outbox = Disk.Read(Path.Combine(directory, "outbox.json"), ProtocolJson.Default.Outbox)!;
            Console.WriteLine($"TEST_REPORT {outbox.State}");
            return outbox.State is "acknowledged" or "localOnly" ? 0 : 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine($"TEST_REPORT_FAILED: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static void ValidateTestReport(ReportConfiguration report)
    {
        if (!Uri.TryCreate(report.Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            report.Type is not (1 or 2) || report.CredentialEnvironmentVariable != null)
            throw new InvalidDataException("Test reporting requires a literal HTTP loopback address.");
    }
}
