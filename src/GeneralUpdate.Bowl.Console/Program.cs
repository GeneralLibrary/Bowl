namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     Entry point of the independently installed crash-diagnosis watchdog
///     (docs/bowl-redesign.md): <c>watch</c> supervises the target process for its whole life
///     cycle, <c>stop</c> asks a running instance to exit. The watchdog has NO network access —
///     GeneralUpdate.Core is the single upload egress for the evidence outbox.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(WatchOptions.Usage());
            return ExitCodes.InvalidArguments;
        }

        foreach (var arg in args)
        {
            if (!arg.Equals("--help", StringComparison.OrdinalIgnoreCase) &&
                !arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                continue;

            Console.WriteLine(WatchOptions.Usage());
            return ExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "watch" => await WatchCommand.RunAsync(args.Skip(1).ToArray()),
                "stop" => StopCommand.Run(args.Skip(1).ToArray()),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            // The watchdog runs unattended — a fatal error must leave a trace, not a stack
            // trace on a console nobody watches. Exit code 3: the run could not continue.
            GeneralTracer.Error("bowl: fatal error.", ex);
            Console.Error.WriteLine($"bowl could not continue: {ex.GetType().Name}.");
            return ExitCodes.InvalidArguments;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine(WatchOptions.Usage());
        return ExitCodes.InvalidArguments;
    }
}
