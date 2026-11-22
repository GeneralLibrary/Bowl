using GeneralUpdate.Bowl.ConsoleApp;

namespace Bowl.Console.Tests;

/// <summary>
///     CLI contract tests: option parsing, config-file precedence (command line &gt; bowl.json
///     &gt; defaults), schema negotiation and validation failures.
/// </summary>
public class CliOptionsTests : IDisposable
{
    [Theory]
    [InlineData("../other")]
    [InlineData("..\\other")]
    [InlineData(".locks")]
    [InlineData("a:b")]
    public void Parse_VersionMustBePortableDirectoryName(string version)
    {
        var options = WatchOptions.Parse(new[] { "--target-name", "App", "--install-path", "app", "--version", version }, out var errors);
        Assert.Null(options);
        Assert.Contains(errors, error => error.Contains("single portable directory name"));
    }

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bowl-cli-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best effort */ }
    }

    private string WriteConfig(string json, string? fileName = null)
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, fileName ?? "bowl.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Parse_FullCommandLine_AllOptionsBound()
    {
        var options = WatchOptions.Parse(new[]
        {
            "--target-name", "MyApp",
            "--install-path", @"D:\apps\MyApp",
            "--version", "1.0.2",
            "--schema", "2",
            "--bowl-home", @"D:\bowl",
            "--dump-type", "Heap",
            "--idle-exit", "01:30:00",
            "--on-crash", "restore.cmd",
            "--log-level", "Off",
        }, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal("MyApp", options!.TargetName);
        Assert.Equal(@"D:\apps\MyApp", options.InstallPath);
        Assert.Equal("1.0.2", options.Version);
        Assert.Equal(2, options.Schema);
        Assert.Equal(@"D:\bowl", options.BowlHome);
        Assert.Equal("Heap", options.DumpType);
        Assert.Equal(TimeSpan.FromHours(1.5), options.IdleExit);
        Assert.Equal("restore.cmd", options.OnCrash);
        Assert.Equal("Off", options.LogLevel);
    }

    [Fact]
    public void Parse_Defaults_WhenOnlyRequiredOptionsGiven()
    {
        // Hermetic: the run directory may carry the default bowl.json (autostart template),
        // so point --config at an empty file to exercise the built-in defaults only.
        var configPath = WriteConfig("{ }");

        var options = WatchOptions.Parse(new[]
        {
            "--config", configPath,
            "--target-name", "MyApp",
            "--install-path", @"D:\apps\MyApp",
            "--version", "1.0.2",
        }, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal(2, options!.Schema);
        Assert.Equal("Mini", options.DumpType);
        Assert.Equal(TimeSpan.FromMinutes(30), options.IdleExit);
        Assert.Equal("Information", options.LogLevel);
        Assert.Null(options.OnCrash);
        Assert.Equal("1.0.2_fail.dmp", options.DumpFileName);
        Assert.Equal("1.0.2_fail.json", options.FailFileName);
    }

    [Fact]
    public void Parse_NoArguments_UsesDefaultConfigNextToExecutable()
    {
        // Autostart contract (§2.1 管理入口): `bowl watch` without arguments boots from the
        // default bowl.json next to the executable — the project template ships with the
        // test output, so this also verifies schema negotiation on the template itself.
        var options = WatchOptions.Parse(Array.Empty<string>(), out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal("MyApp", options!.TargetName);
        Assert.Equal(AppContext.BaseDirectory, options.BowlHome);
    }

    [Fact]
    public void Parse_MissingRequiredFields_ReportsErrorsAndReturnsNull()
    {
        // A nonexistent explicit config + empty arguments: nothing can satisfy the required
        // fields, so validation must fail with one error per missing field.
        var options = WatchOptions.Parse(new[]
        {
            "--config", Path.Combine(_tempDirectory, "absent.json"),
        }, out var errors);

        Assert.Null(options);
        Assert.Contains(errors, e => e.Contains("--target-name"));
        Assert.Contains(errors, e => e.Contains("--install-path"));
        Assert.Contains(errors, e => e.Contains("--version"));
    }

    [Fact]
    public void Parse_ForeignSchema_Refused()
    {
        var options = WatchOptions.Parse(new[]
        {
            "--target-name", "MyApp",
            "--install-path", @"D:\apps",
            "--version", "1.0.2",
            "--schema", "3",
        }, out var errors);

        Assert.Null(options);
        Assert.Contains(errors, e => e.Contains("schema"));
    }

    [Fact]
    public void Parse_InvalidIdleExitInConfig_ReportsError()
    {
        // Regression: an unparseable bowl.json idleExit used to be silently ignored —
        // the operator believed the setting was active while the default applied. The
        // config path must surface the same error treatment the CLI flag gets.
        var configPath = WriteConfig(
            "{\"schema\":2,\"targetName\":\"MyApp\",\"installPath\":\"D:\\\\apps\",\"version\":\"1.0.2\",\"idleExit\":\"not-a-timespan\"}");

        var options = WatchOptions.Parse(new[] { "--config", configPath }, out var errors);

        Assert.Null(options);
        Assert.Contains(errors, e => e.Contains("idleExit") && e.Contains("not-a-timespan"));
    }

    [Fact]
    public void Parse_ValidIdleExitInConfig_StillAccepted()
    {
        var configPath = WriteConfig(
            "{\"schema\":2,\"targetName\":\"MyApp\",\"installPath\":\"D:\\\\apps\",\"version\":\"1.0.2\",\"idleExit\":\"00:45:00\"}");

        var options = WatchOptions.Parse(new[] { "--config", configPath }, out var errors);

        Assert.NotNull(options);
        Assert.Empty(errors);
        Assert.Equal(TimeSpan.FromMinutes(45), options!.IdleExit);
    }

    [Fact]
    public void Parse_UnknownOption_Refused()
    {
        var options = WatchOptions.Parse(new[]
        {
            "--target-name", "MyApp", "--install-path", @"D:\apps", "--version", "1.0.2",
            "--wat", "value",
        }, out var errors);

        Assert.Null(options);
        Assert.Contains(errors, e => e.Contains("--wat"));
    }

    [Fact]
    public void Parse_ConfigFile_FillsUngivenValues()
    {
        var configPath = WriteConfig(""" 
            {
              "schema": 2,
              "targetName": "ConfigApp",
              "installPath": "C:\\from-config",
              "version": "9.9.9",
              "dumpType": "Full",
              "idleExit": "00:05:00"
            }
            """);

        var options = WatchOptions.Parse(new[]
        {
            "--config", configPath,
        }, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal("ConfigApp", options!.TargetName);
        Assert.Equal(@"C:\from-config", options.InstallPath);
        Assert.Equal("9.9.9", options.Version);
        Assert.Equal("Full", options.DumpType);
        Assert.Equal(TimeSpan.FromMinutes(5), options.IdleExit);
    }

    [Fact]
    public void Parse_CommandLineWinsOverConfigFile()
    {
        var configPath = WriteConfig(""" 
            {
              "targetName": "ConfigApp",
              "installPath": "C:\\from-config",
              "version": "9.9.9"
            }
            """);

        var options = WatchOptions.Parse(new[]
        {
            "--config", configPath,
            "--target-name", "CliApp",
        }, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal("CliApp", options!.TargetName);
        Assert.Equal(@"C:\from-config", options.InstallPath);
        Assert.Equal("9.9.9", options.Version);
    }

    [Fact]
    public void Parse_ConfigWithForeignSchema_Refused()
    {
        var configPath = WriteConfig(""" 
            {
              "schema": 3,
              "targetName": "ConfigApp",
              "installPath": "C:\\x",
              "version": "1.0.0"
            }
            """);

        var options = WatchOptions.Parse(new[] { "--config", configPath }, out var errors);

        Assert.Null(options);
        Assert.Contains(errors, e => e.Contains("schema"));
    }

    [Fact]
    public void Parse_MissingExplicitConfigPath_Reported()
    {
        var options = WatchOptions.Parse(new[]
        {
            "--config", Path.Combine(_tempDirectory, "nope.json"),
        }, out var errors);

        Assert.Null(options);
        Assert.Contains(errors, e => e.Contains("nope.json"));
    }
}
