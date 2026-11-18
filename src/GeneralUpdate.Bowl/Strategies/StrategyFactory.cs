using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GeneralUpdate.Bowl.Strategies;

/// <summary>
/// Creates the correct <see cref="IBowlStrategy"/> for the current operating system.
/// Replaces the inline platform detection in the old static Bowl class.
/// </summary>
internal static class StrategyFactory
{
    /// <summary>
    /// Creates a platform-appropriate strategy.
    /// Throws <see cref="PlatformNotSupportedException"/> if no strategy is available.
    /// </summary>
    public static IBowlStrategy Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsBowlStrategy();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxBowlStrategy();

        throw new PlatformNotSupportedException(
            $"Bowl does not support the current platform: {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    ///     Three-tier watcher selection with graceful degradation (docs/bowl-redesign.md §4.4):
    ///     procdump (bundled or installed) → runtime createdump → exit-code-only fallback.
    ///     The embedded single-shot API (<see cref="BowlBootstrap"/> via <see cref="Create"/>)
    ///     is unchanged; this overload exists for the console supervisor, which re-arms
    ///     sessions and needs a watcher on every platform — including macOS, which used to
    ///     throw and now at least produces crash reports.
    /// </summary>
    /// <param name="tier">Selected tier — surfaced for logs and diagnostics.</param>
    public static IBowlStrategy CreateWithFallback(out WatcherTier tier)
    {
        if (TryCreateProcdump())
        {
            tier = WatcherTier.Procdump;
            return Create();
        }

        if (CreatedumpStrategy.IsCreatedumpAvailable())
        {
            tier = WatcherTier.Createdump;
            return new CreatedumpStrategy();
        }

        tier = WatcherTier.ExitCode;
        return new ExitCodeWatcherStrategy();
    }

    /// <summary>Probes whether the procdump tool of the current platform can be resolved.</summary>
    private static bool TryCreateProcdump()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Same resolution as WindowsBowlStrategy: architecture-specific binary shipped in
            // Applications/Windows next to the executable (Bowl's own home directory).
            var procdumpExe = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "procdump.exe",
                Architecture.X64 => "procdump64.exe",
                _ => "procdump64a.exe",
            };
            return File.Exists(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Applications", "Windows", procdumpExe));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // LinuxBowlStrategy probes (and installs) procdump lazily in Prepare; treat Linux
            // procdump as tier 1 — a failed install degrades to null Prepare, which the
            // console supervisor handles by falling back to the exit-code verdict per session.
            return true;
        }

        return false; // macOS: never had procdump support.
    }
}
