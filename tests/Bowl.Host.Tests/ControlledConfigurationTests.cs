using System.Security.Principal;
using System.Text.Json;
using Bowl.Host;
using System.Diagnostics;

namespace Bowl.Host.Tests;

public sealed class ControlledConfigurationTests
{
    [Fact]
    public void ConfigurationInWritableUserDirectoryCannotAuthorizeService()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        using var identity = WindowsIdentity.GetCurrent();
        var path = Path.Combine(f.Base, "service.json");
        var settings = new ControlledSettings
        {
            ServiceAccountSid = identity.User!.Value,
            InstallationRoot = f.Request.InstallPath,
            RegistryRoot = Path.Combine(f.Base, "registry"),
            StateRoot = f.Root,
            WorkerExecutable = Path.Combine(f.Base, "worker.exe")
        };
        File.WriteAllText(path, JsonSerializer.Serialize(settings, ControlledSettingsJson.Default.ControlledSettings));
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows())
                ControlledConfiguration.Load(path);
        });
    }

    [Fact]
    public async Task ControlledCheckInUntrustedDirectoryRefusesExecution()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Base, "service.json");
        File.WriteAllText(path, "{}");
        var hostPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Bowl.Host", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.Host.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { hostPath, "--controlled-check", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, process.ExitCode);
        Assert.Contains("Bowl could not complete", await process.StandardError.ReadToEndAsync());
    }
}
