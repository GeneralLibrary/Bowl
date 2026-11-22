using System.Diagnostics;

namespace Bowl.Host.Tests;

[Collection("Controlled lease processes")]
public sealed class ControlledWorkerTests
{
    [Fact]
    public async Task HostDeathTerminatesWorkerThroughJob()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var peerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Bowl.TestPeer", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.TestPeer.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in new[] { peerPath, "job-parent", peerPath })
            start.ArgumentList.Add(argument);
        using var parent = Process.Start(start)!;
        Process? child = null;
        try
        {
            var line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(int.TryParse(line, out var pid), "Job parent did not report a worker PID.");
            child = Process.GetProcessById(pid);
            Assert.False(child.HasExited);
            parent.Kill();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill();
                await parent.WaitForExitAsync();
            }
            if (child != null)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
        }
    }
}
