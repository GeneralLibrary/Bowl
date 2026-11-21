namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     The <c>stop</c> command (§4.7 uninstall path): writes the stop-signal file into the
///     bowl home; a running supervisor loop notices it within one poll cycle and exits with
///     code 0. File-based by design — no IPC channel, consistent with the disk-contract
///     philosophy ("no IPC, no shared state" between components; this file is Bowl's own).
/// </summary>
internal static class StopCommand
{
    public static int Run(string[] args)
    {
        string? bowlHome = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--bowl-home", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("bowl stop: --bowl-home requires a directory path.");
                    return ExitCodes.InvalidArguments;
                }

                bowlHome = args[++i];
            }
            else if (args[i].Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(WatchOptions.Usage());
                return ExitCodes.Success;
            }
        }

        var home = bowlHome ?? AppContext.BaseDirectory;
        try
        {
            Directory.CreateDirectory(home);
            OutboxManager.RequestStop(home);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bowl stop: could not write stop signal into '{home}': {ex.Message}");
            return ExitCodes.InvalidArguments;
        }

        Console.WriteLine($"bowl: stop requested via '{Path.Combine(home, "stop.signal")}' — the watchdog exits within one poll cycle.");
        return ExitCodes.Success;
    }
}
