using System.Globalization;
using System.Text.Json;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>Exit codes of the watchdog console (docs/bowl-redesign.md §4.2).</summary>
internal static class ExitCodes
{
    /// <summary>Normal exit (idle-exit reached, stop requested, or stop command done).</summary>
    public const int Success = 0;

    /// <summary>Single-instance lock already held — another watchdog instance owns this target.</summary>
    public const int AlreadyRunning = 2;

    /// <summary>Invalid command line / configuration.</summary>
    public const int InvalidArguments = 3;

    /// <summary>Watcher initialisation failed with no fallback (reserved — the exit-code tier always exists).</summary>
    public const int WatcherUnavailable = 4;
}

/// <summary>
///     Resolved watchdog configuration. Resolution order: command line &gt; bowl.json &gt; built-in
///     defaults — every field of the CLI contract (§2.2) maps 1:1 onto this type, which is then
///     assembled into the library's <see cref="BowlContext"/>. The old "the host just knows what
///     to watch" convention is gone: everything arrives through this contract.
/// </summary>
internal sealed class WatchOptions
{
    /// <summary>Contract version this build speaks. A foreign <c>--schema</c> value is refused (exit 3).</summary>
    public const int SupportedSchema = 2;

    /// <summary>Watched process name (no extension) — half of the single-instance key.</summary>
    public string TargetName { get; private set; } = string.Empty;

    /// <summary>Installation root of the watched application (outbox root = <c>{install}/fail/</c>).</summary>
    public string InstallPath { get; private set; } = string.Empty;

    /// <summary>Watched application version (evidence sub-directory + report field).</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Contract version requested by the launcher (Core passes 2).</summary>
    public int Schema { get; private set; } = SupportedSchema;

    /// <summary>Bowl's own home (logs, tool cache, stop signal). Defaults to the executable directory.</summary>
    public string BowlHome { get; private set; } = AppContext.BaseDirectory;

    /// <summary>Dump level: Mini (default) | Heap | Full.</summary>
    public string DumpType { get; private set; } = "Mini";

    /// <summary>How long the target may stay absent before the watchdog exits on its own.</summary>
    public TimeSpan IdleExit { get; private set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Advanced hook: external command executed after crash evidence is written (replaces the
    ///     removed AutoRestore semantics — users who want crash-triggered rollback wire their own
    ///     restore script and own the risk). The evidence directory is appended as an argument.
    /// </summary>
    public string? OnCrash { get; private set; }

    /// <summary>Log verbosity (Off disables tracing; the library tracer has no finer-grained filter).</summary>
    public string LogLevel { get; private set; } = "Information";

    /// <summary>File name of the dump inside a session directory.</summary>
    public string DumpFileName => $"{Version}_fail.dmp";

    /// <summary>File name of the crash report inside a session directory.</summary>
    public string FailFileName => $"{Version}_fail.json";

    /// <summary>Stop-signal file polled by the supervisor loop (written by <c>bowl stop</c>).</summary>
    public string StopSignalPath => Path.Combine(BowlHome, "stop.signal");

    /// <summary>Default configuration file location (no-argument boot / autostart scenario).</summary>
    public static string DefaultConfigPath => Path.Combine(AppContext.BaseDirectory, "bowl.json");

    // ── Parser ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Hand-written parser (no third-party CLI dependency). Tokens are <c>--key value</c>
    ///     pairs; <c>--help</c> is a flag. Values from the configuration file fill anything the
    ///     command line did not set, so explicit CLI values always win.
    /// </summary>
    /// <returns>The resolved options, or <c>null</c> when validation failed (errors populated).</returns>
    public static WatchOptions? Parse(string[] args, out List<string> errors)
    {
        errors = new List<string>();
        var cli = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? configPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                errors.Add($"Unexpected argument '{token}' — options are --key value pairs.");
                continue;
            }

            var key = token.Substring(2);
            if (key.Equals("help", StringComparison.OrdinalIgnoreCase))
                continue; // handled by the caller before parsing

            if (key.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) errors.Add("--config requires a file path.");
                else configPath = args[++i];
                continue;
            }

            if (i + 1 >= args.Length)
            {
                errors.Add($"--{key} requires a value.");
                continue;
            }

            cli[key] = args[++i];
        }

        // 1. Built-in defaults.
        var options = new WatchOptions();

        // 2. Configuration file (explicit --config, or the default next to the executable).
        var effectiveConfigPath = configPath ?? DefaultConfigPath;
        if (File.Exists(effectiveConfigPath))
            ApplyConfigFile(options, effectiveConfigPath, errors);
        else if (configPath != null)
            errors.Add($"Config file not found: {configPath}.");

        // 3. Command line overrides.
        ApplyCommandLine(options, cli, errors);

        // 4. Required-value validation.
        if (string.IsNullOrWhiteSpace(options.TargetName))
            errors.Add("--target-name is required (watched process name).");
        if (string.IsNullOrWhiteSpace(options.InstallPath))
            errors.Add("--install-path is required (watched application install root).");
        if (string.IsNullOrWhiteSpace(options.Version))
            errors.Add("--version is required (watched application version).");
        if (options.Schema != SupportedSchema)
            errors.Add($"Unsupported --schema {options.Schema} (this build speaks schema {SupportedSchema}).");
        if (!IsKnownDumpType(options.DumpType))
            errors.Add($"Unknown --dump-type '{options.DumpType}' (Mini | Heap | Full).");

        return errors.Count == 0 ? options : null;
    }

    private static void ApplyCommandLine(WatchOptions options, Dictionary<string, string> cli, List<string> errors)
    {
        foreach (var (key, value) in cli)
        {
            switch (key)
            {
                case "target-name": options.TargetName = value; break;
                case "install-path": options.InstallPath = value; break;
                case "version": options.Version = value; break;
                case "schema":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schema))
                        errors.Add($"--schema must be an integer, got '{value}'.");
                    else
                        options.Schema = schema;
                    break;
                case "bowl-home": options.BowlHome = value; break;
                case "dump-type": options.DumpType = value; break;
                case "idle-exit":
                    if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var idle))
                        errors.Add($"--idle-exit must be hh:mm:ss, got '{value}'.");
                    else
                        options.IdleExit = idle;
                    break;
                case "on-crash": options.OnCrash = value; break;
                case "log-level": options.LogLevel = value; break;
                default:
                    errors.Add($"Unknown option --{key}.");
                    break;
            }
        }
    }

    private static void ApplyConfigFile(WatchOptions options, string path, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Config file must contain a JSON object: {path}.");
                return;
            }

            // Schema negotiation: a config written for another contract version is refused so
            // an old console never misparses a newer file (and vice versa).
            if (root.TryGetProperty("schema", out var schemaElement) &&
                schemaElement.ValueKind == JsonValueKind.Number &&
                schemaElement.GetInt32() != SupportedSchema)
            {
                errors.Add($"Config schema {schemaElement.GetInt32()} not supported (this build speaks schema {SupportedSchema}): {path}.");
                return;
            }

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "targetName" when property.Value.ValueKind == JsonValueKind.String:
                        options.TargetName = property.Value.GetString()!; break;
                    case "installPath" when property.Value.ValueKind == JsonValueKind.String:
                        options.InstallPath = property.Value.GetString()!; break;
                    case "version" when property.Value.ValueKind == JsonValueKind.String:
                        options.Version = property.Value.GetString()!; break;
                    case "schema" when property.Value.ValueKind == JsonValueKind.Number:
                        options.Schema = property.Value.GetInt32(); break;
                    case "bowlHome" when property.Value.ValueKind == JsonValueKind.String:
                        options.BowlHome = property.Value.GetString()!; break;
                    case "dumpType" when property.Value.ValueKind == JsonValueKind.String:
                        options.DumpType = property.Value.GetString()!; break;
                    case "idleExit" when property.Value.ValueKind == JsonValueKind.String:
                        if (TimeSpan.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, out var idle))
                            options.IdleExit = idle;
                        break;
                    case "onCrash" when property.Value.ValueKind == JsonValueKind.String:
                        options.OnCrash = property.Value.GetString(); break;
                    case "logLevel" when property.Value.ValueKind == JsonValueKind.String:
                        options.LogLevel = property.Value.GetString()!; break;
                    // Unknown keys are ignored: the config stays forward/backward tolerant.
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            errors.Add($"Cannot read config file {path}: {ex.GetType().Name}.");
        }
    }

    private static bool IsKnownDumpType(string value) =>
        value.Equals("Mini", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Heap", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Full", StringComparison.OrdinalIgnoreCase);

    // ── Usage ───────────────────────────────────────────────────────────

    public static string Usage() =>
        """
        bowl — independently installed crash-diagnosis watchdog (schema 2)

        Usage:
          bowl watch --target-name <name> --install-path <dir> --version <ver> [--schema 2]
                     [--bowl-home <dir>] [--dump-type Mini|Heap|Full] [--idle-exit hh:mm:ss]
                     [--config <file>] [--on-crash <command>] [--log-level <level>]
          bowl watch                          (no arguments: read the default bowl.json)
          bowl stop  [--bowl-home <dir>]      (ask a running watchdog to exit gracefully)

        Exit codes:
          0  normal exit          2  already running (single-instance lock held)
          3  invalid arguments    4  watcher unavailable without fallback

        Contract: CLI arguments + the {install}/fail/{version}/{guid}/ outbox on disk.
        The watchdog has no network access; GeneralUpdate.Core uploads the evidence.
        """;
}
