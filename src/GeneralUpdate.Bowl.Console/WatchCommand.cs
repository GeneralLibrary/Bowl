using GeneralUpdate.Bowl;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     The <c>watch</c> command: resolve options → honour the single-instance lock → select the
///     watcher tier → run the supervisor loop. Any failure maps onto the documented exit codes.
/// </summary>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = WatchOptions.Parse(args, out var errors);
        if (options == null)
        {
            Console.Error.WriteLine("bowl watch: invalid arguments/configuration.");
            foreach (var error in errors)
                Console.Error.WriteLine($"  - {error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(WatchOptions.Usage());
            return ExitCodes.InvalidArguments;
        }

        // Contract negotiation (§2.2): this build refuses a foreign schema instead of guessing.
        if (options.Schema != WatchOptions.SupportedSchema)
        {
            Console.Error.WriteLine(
                $"bowl watch: unsupported contract schema {options.Schema} (this build speaks schema {WatchOptions.SupportedSchema}).");
            return ExitCodes.InvalidArguments;
        }

        ConfigureLogging(options.LogLevel);

        using (var lockHandle = SingleInstanceLock.Acquire(options.TargetName, options.InstallPath))
        {
            if (lockHandle == null)
            {
                // Documented "already running" case: a normal, successful outcome for whoever
                // started the duplicate (Core treats it as watchdog-is-running).
                GeneralTracer.Info(
                    $"WatchCommand: watchdog already running for target '{options.TargetName}' — exiting (code 2).");
                return ExitCodes.AlreadyRunning;
            }

            var watcher = WatcherSelector.Select(out var tier);
            if (watcher == null)
            {
                // Reserved: the exit-code tier makes selection failure impossible today.
                GeneralTracer.Error("WatchCommand: no watcher available on this platform.");
                return ExitCodes.WatcherUnavailable;
            }

            var outbox = new OutboxManager(options);
            var loop = new SupervisorLoop(options, outbox, watcher);
            return await loop.RunAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Honours <c>--log-level</c> with the capability the library tracer actually has: a
    ///     binary on/off switch (Off/None disables). Finer levels are accepted for contract
    ///     compatibility and reported as such — silently degrading to a different verbosity
    ///     would be worse than saying so.
    /// </summary>
    private static void ConfigureLogging(string logLevel)
    {
        if (logLevel.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
            logLevel.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            GeneralTracer.SetTracingEnabled(false);
            return;
        }

        if (!logLevel.Equals("Information", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"bowl: --log-level '{logLevel}' accepted; the tracer supports Off/Information granularity only.");
        }
    }
}
