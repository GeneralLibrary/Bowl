using System.ComponentModel;
using System.Text.Json;

namespace Bowl.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 4 || args[2] != "--state-root" ||
                args[0] is not ("--attempt" or "--retry"))
                throw new ArgumentException("Use --attempt <GUID> --state-root <absolute-path> or --retry <limit:1-100> --state-root <absolute-path>.");
            var root = Paths.Absolute(args[3]);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler);
            var supervisor = new Supervisor(new Processes(), new Delivery(client));
            if (args[0] == "--attempt")
            {
                var result = await supervisor.RunAsync(root, args[1], CancellationToken.None);
                return result.Rollback == "deferred" ? 2 : result.Outcome == "success" ? 0 : 1;
            }
            if (!int.TryParse(args[1], out var limit) || limit is < 1 or > 100)
                throw new ArgumentException("Retry limit must be between 1 and 100.");
            var failures = 0;
            var processed = 0;
            var scanned = 0;
            // On-demand, bounded sweeps; no service is installed. Skip live
            // attempts via their exclusive lock, never by deleting their lock.
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "attempts")))
            {
                if (++scanned > 10_000)
                {
                    Console.Error.WriteLine("Retry scan capacity reached; archive acknowledged history.");
                    failures++;
                    break;
                }
                if (!File.Exists(Path.Combine(directory, "result.json")))
                    continue; // Incomplete observation resumes only via --attempt.
                try
                {
                    var outbox = Disk.Read(Path.Combine(directory, "outbox.json"), ProtocolJson.Default.Outbox);
                    if (outbox != null)
                    {
                        Disk.Validate(outbox, Path.GetFileName(directory));
                        if (outbox.State is "acknowledged" or "localOnly" ||
                            outbox.NextAttemptUtc > DateTimeOffset.UtcNow)
                            continue;
                    }
                    if (++processed > limit)
                    {
                        Console.Error.WriteLine("Retry batch limit reached; run another sweep for remaining attempts.");
                        failures++;
                        break;
                    }
                    await supervisor.RunAsync(root, Path.GetFileName(directory), CancellationToken.None);
                }
                catch (Exception ex) when (IsOperational(ex))
                {
                    failures++;
                    Console.Error.WriteLine($"Attempt retry refused or unavailable: {ex.GetType().Name}.");
                }
            }
            return failures == 0 ? 0 : 2;
        }
        catch (Exception ex) when (IsOperational(ex))
        {
            // Do not print arbitrary exception messages or serialized requests.
            Console.Error.WriteLine($"Bowl could not complete: {ex.GetType().Name}.");
            if (ex is InvalidDataException)
                Console.Error.WriteLine(ex.Message);
            if (ex is ArgumentException)
                Console.Error.WriteLine("Usage: Bowl.Host --attempt <GUID> --state-root <absolute-path>; --retry <limit:1-100> --state-root <absolute-path>");
            return 2;
        }
    }

    private static bool IsOperational(Exception ex) => ex is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or JsonException or
        Win32Exception or InvalidOperationException;
}
