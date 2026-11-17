using System.Diagnostics;
using GeneralUpdate.Bowl.Strategies;

namespace BowlTest.Strategies;

/// <summary>
/// Unit tests for <see cref="ProcessRunner"/> following AAAT pattern.
/// Tests process execution, output capture, timeout, and cancellation.
/// </summary>
public class ProcessRunnerTests
{
    #region RunAsync — successful execution

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsExitCodeZero()
    {
        var result = await ProcessRunner.RunAsync(ShellExitZero(), timeoutMs: 10000);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_ReturnsCorrectExitCode()
    {
        var result = await ProcessRunner.RunAsync(ShellExitSeven(), timeoutMs: 10000);

        Assert.Equal(7, result.ExitCode);
    }

    #endregion

    #region RunAsync — output capture

    [Fact]
    public async Task RunAsync_CapturesStandardOutput()
    {
        var result = await ProcessRunner.RunAsync(ShellEchoProcessRunnerTest(), timeoutMs: 10000);

        Assert.Contains(result.OutputLines, line => line.Contains("ProcessRunnerTest"));
    }

    [Fact]
    public async Task RunAsync_OutputLines_IsNotNull()
    {
        var result = await ProcessRunner.RunAsync(ShellEchoHello(), timeoutMs: 10000);

        Assert.NotNull(result.OutputLines);
    }

    #endregion

    #region RunAsync — failed process start

    [Fact]
    public async Task RunAsync_NonExistentCommand_ThrowsException()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nonexistent_command_xyz_123.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => ProcessRunner.RunAsync(psi, timeoutMs: 5000));
    }

    #endregion

    #region RunAsync — timeout

    [Fact]
    public async Task RunAsync_Timeout_ThrowsTimeoutException()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => ProcessRunner.RunAsync(ShellBlocker(), timeoutMs: 500));
    }

    #endregion

    #region RunAsync — cancellation

    [Fact]
    public async Task RunAsync_Cancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.RunAsync(ShellBlocker(), timeoutMs: 30000, cts.Token));
    }

    #endregion

    #region RunAsync — structured result

    [Fact]
    public async Task RunAsync_ResultHasOutputLines()
    {
        var result = await ProcessRunner.RunAsync(ShellEchoLine(), timeoutMs: 10000);

        Assert.NotEmpty(result.OutputLines);
    }

    #endregion

    // ---- OS-portable trivial processes ----

    // Lifecycle tests only need processes that exit, echo, or sleep briefly. Both
    // platform command lines are compile-time literals — nothing dynamic is ever
    // interpolated — and POSIX sh takes the whole script as one quoted argument,
    // so the same suite runs on Windows and Linux CI alike.
    private static ProcessStartInfo ShellExitZero() => OperatingSystem.IsWindows()
        ? LiteralProcess("cmd", "/c exit 0")
        : LiteralProcess("/bin/sh", "-c \"exit 0\"");

    private static ProcessStartInfo ShellExitSeven() => OperatingSystem.IsWindows()
        ? LiteralProcess("cmd", "/c exit 7")
        : LiteralProcess("/bin/sh", "-c \"exit 7\"");

    private static ProcessStartInfo ShellEchoProcessRunnerTest() => OperatingSystem.IsWindows()
        ? LiteralProcess("cmd", "/c echo ProcessRunnerTest")
        : LiteralProcess("/bin/sh", "-c \"echo ProcessRunnerTest\"");

    private static ProcessStartInfo ShellEchoHello() => OperatingSystem.IsWindows()
        ? LiteralProcess("cmd", "/c echo hello")
        : LiteralProcess("/bin/sh", "-c \"echo hello\"");

    private static ProcessStartInfo ShellEchoLine() => OperatingSystem.IsWindows()
        ? LiteralProcess("cmd", "/c echo line1")
        : LiteralProcess("/bin/sh", "-c \"echo line1\"");

    private static ProcessStartInfo ShellBlocker() => OperatingSystem.IsWindows()
        ? LiteralProcess("cmd", "/c ping -n 30 127.0.0.1")
        : LiteralProcess("/bin/sh", "-c \"sleep 30\"");

    private static ProcessStartInfo LiteralProcess(string fileName, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
}
