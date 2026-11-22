using System.Text.Json;
using GeneralUpdate.Bowl.ConsoleApp;

namespace Bowl.Console.Tests;

public sealed class OutboxFixtureTests
{
    [Fact]
    public void GenerateProtocol1Fixture()
    {
        var configured = Environment.GetEnvironmentVariable("BOWL_FIXTURE_INSTALL");
        var install = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "bowl-fixture-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configured);
        var temporary = string.IsNullOrWhiteSpace(configured);
        try
        {
            var options = new WatchOptionsBuilder().WithInstallPath(install).WithVersion("1.0.2").Build();
            var manager = new OutboxManager(options);
            using var session = manager.CreateSession();
            Assert.NotNull(session);
            File.WriteAllText(Path.Combine(session.Directory, "evidence.txt"), "Bowl fixture evidence");
            var loop = new SupervisorLoop(options, manager, new ExitCodeWatcher());
            Assert.True(loop.PublishEvidence(session.Directory, new WatchOutcome(-1), null, install));
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.Directory, "manifest.json")));
            Assert.Equal(1, manifest.RootElement.GetProperty("outboxProtocol").GetInt32());
            Assert.Equal("pending", manifest.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            if (temporary && Directory.Exists(install)) Directory.Delete(install, true);
        }
    }
}
