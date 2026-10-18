using System.Runtime.InteropServices;
using GeneralUpdate.Bowl.ConsoleApp;

namespace Bowl.Console.Tests;

/// <summary>
///     Single-instance lock tests: the key algorithm (shared contract with Core's
///     <c>BowlLauncher</c>) and actual mutual exclusion on the current platform.
/// </summary>
public class SingleInstanceLockTests
{
    [Fact]
    public void BuildKey_DeterministicAndFormatConformant()
    {
        var first = SingleInstanceLock.BuildKey("MyApp", @"D:\apps\MyApp");
        var second = SingleInstanceLock.BuildKey("MyApp", @"D:\apps\MyApp");

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.All(first, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void BuildKey_VersionedPayload_DiffersAcrossTargets()
    {
        var a = SingleInstanceLock.BuildKey("AppA", @"D:\apps");
        var b = SingleInstanceLock.BuildKey("AppB", @"D:\apps");
        var c = SingleInstanceLock.BuildKey("AppA", @"D:\other");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Acquire_SecondAcquireForSameTargetIsRefused()
    {
        var target = $"TestApp_{Guid.NewGuid():N}";
        var install = Path.Combine(Path.GetTempPath(), $"bowl-lock-tests-{Guid.NewGuid():N}");

        using var first = SingleInstanceLock.Acquire(target, install);
        Assert.NotNull(first);

        using var second = SingleInstanceLock.Acquire(target, install);
        Assert.Null(second); // "already running" — the harmless duplicate-start contract
    }

    [Fact]
    public void Acquire_DifferentTargetsDoNotInterfere()
    {
        var install = Path.Combine(Path.GetTempPath(), $"bowl-lock-tests-{Guid.NewGuid():N}");
        var prefix = $"TestApp_{Guid.NewGuid():N}";

        using var a = SingleInstanceLock.Acquire($"{prefix}_A", install);
        using var b = SingleInstanceLock.Acquire($"{prefix}_B", install);

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void BuildKey_MatchesCoreContractAlgorithm()
    {
        // Byte-level contract check against the algorithm documented in
        // docs/bowl-redesign.md §5 (单实例锁): SHA-256 over a versioned payload,
        // hex-truncated to 32 chars. Verified independently of the production code.
        var target = "MyApp";
        var install = @"D:\apps\MyApp";
        var ignoreCase = RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        var normalizedPath = Path.GetFullPath(install).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (ignoreCase) normalizedPath = normalizedPath.ToLowerInvariant();
        var normalizedTarget = ignoreCase ? target.ToLowerInvariant() : target;

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
            $"GeneralUpdate.Bowl.v2|{normalizedTarget}|{normalizedPath}"));
        var expected = Convert.ToHexString(hash).ToLowerInvariant()[..32];

        Assert.Equal(expected, SingleInstanceLock.BuildKey(target, install));
    }
}
