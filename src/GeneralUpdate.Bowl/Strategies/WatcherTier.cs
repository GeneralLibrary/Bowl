namespace GeneralUpdate.Bowl.Strategies;

/// <summary>
///     Watcher capability tiers, resolved once at watchdog startup by
///     <see cref="StrategyFactory.CreateWithFallback"/> (docs/bowl-redesign.md §4.4).
/// </summary>
/// <remarks>
///     Tiers degrade gracefully: a lower tier loses evidence quality (fewer/no dumps) but never
///     loses the crash verdict itself — the exit-code decision always runs in the supervisor loop.
/// </remarks>
internal enum WatcherTier
{
    /// <summary>Tier 1 — procdump child process, full dump on first-chance exceptions (bundled or installed).</summary>
    Procdump = 1,

    /// <summary>
    ///     Tier 2 — .NET runtime's own <c>createdump</c> (no extra distribution payload). The
    ///     runtime writes a dump only when crash-dump environment configuration is enabled;
    ///     this tier waits for the target to exit and collects the runtime-produced dump file
    ///     when one exists. Crash report and system info are produced regardless.
    /// </summary>
    Createdump = 2,

    /// <summary>
    ///     Tier 3 — exit-code verdict only (all-platform fallback). No dump, but crash report
    ///     and system info are still produced, so macOS goes from "unsupported" to "at least
    ///     a crash report".
    /// </summary>
    ExitCode = 3,
}
