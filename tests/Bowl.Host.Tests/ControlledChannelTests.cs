using System.Diagnostics;
using System.IO.Pipes;
using Bowl.Host;

namespace Bowl.Host.Tests;

[Collection("Controlled lease processes")]
public sealed class ControlledChannelTests
{
    [Fact]
    public async Task ServerChecksActualClientProcessIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var name = "bowl-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var peerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Bowl.TestPeer", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net8.0", "Bowl.TestPeer.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        using var current = Process.GetCurrentProcess();
        foreach (var argument in new[]
        {
            peerPath, "pipe-peer", name, current.Id.ToString(),
            current.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            var identity = new ProcessIdentity(child.Id, child.StartTime.ToUniversalTime());
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var authenticated = ControlledChannel.AuthenticateClient(pipe, identity);
            Assert.Equal(child.Id, authenticated.Id);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            Assert.Equal("VERIFIED", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Throws<InvalidDataException>(() =>
            {
                if (OperatingSystem.IsWindows())
                    using (ControlledChannel.AuthenticateClient(pipe, identity with { Pid = Environment.ProcessId })) { }
            });
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }
}
