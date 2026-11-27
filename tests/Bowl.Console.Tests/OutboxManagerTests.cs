using System.Text.Json;
using GeneralUpdate.Bowl.ConsoleApp;

namespace Bowl.Console.Tests;

/// <summary>
///     Outbox contract tests: publication, retention and bounded admission.
/// </summary>
public class OutboxManagerTests : IDisposable
{
    private readonly string _installPath = Path.Combine(
        Path.GetTempPath(), $"bowl-outbox-tests-{Guid.NewGuid():N}", "app");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_installPath)!, recursive: true); }
        catch { /* best effort */ }
    }

    private OutboxManager CreateManager() => new(new WatchOptionsBuilder()
        .WithInstallPath(_installPath)
        .WithVersion("1.0.2")
        .Build());

    [Fact]
    public void CreateSessionDirectory_GuidNamedUnderFailRoot()
    {
        var manager = CreateManager();
        using var lease = manager.CreateSession()!;
        var session = lease.Directory;

        Assert.True(Directory.Exists(session));
        Assert.Equal(Path.Combine(_installPath, "fail", "1.0.2"), Path.GetDirectoryName(session));
        Assert.True(Guid.TryParseExact(Path.GetFileName(session), "D", out _));
    }

    [Fact]
    public void WriteManifest_Schema2ContractShape()
    {
        var manager = CreateManager();
        using var lease = manager.CreateSession()!;
        var session = lease.Directory;
        File.WriteAllText(Path.Combine(session, "1.0.2_fail.json"), "{}");
        File.WriteAllText(Path.Combine(session, "sysinfo.txt"), "sys");

        manager.WriteManifest(session, new WatchOutcome(-1073741819));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("schema").GetInt32());
        Assert.Equal(1, root.GetProperty("outboxProtocol").GetInt32());
        Assert.Equal(Path.GetFileName(session), root.GetProperty("guid").GetString());
        Assert.Equal("crash", root.GetProperty("kind").GetString());
        Assert.Equal("1.0.2", root.GetProperty("version").GetString());
        Assert.Equal(-1073741819, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("pending", root.GetProperty("state").GetString());
        Assert.Equal(0, root.GetProperty("attempts").GetInt32());

        var files = root.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("1.0.2_fail.json", files);
        Assert.Contains("sysinfo.txt", files);
        Assert.DoesNotContain("manifest.json", files); // the manifest never lists itself
    }

    [Fact]
    public void WriteManifest_LeavesNoTempResidue_AndIsOverwritable()
    {
        // The manifest is what makes a crash case visible to the outbox sweep, written
        // at the least stable moment (post-crash). Publishing must clean up the sibling
        // temp file and tolerate a repeated write over an existing manifest (re-capture
        // in the same session must not fail on the rename).
        var manager = CreateManager();
        using var lease = manager.CreateSession()!;
        var session = lease.Directory;
        File.WriteAllText(Path.Combine(session, "1.0.2_fail.json"), "{}");

        manager.WriteManifest(session, new WatchOutcome(-1073741819));
        Assert.True(File.Exists(Path.Combine(session, "manifest.json")));
        Assert.Empty(Directory.GetFiles(session, "manifest.json.tmp*"));

        manager.WriteManifest(session, new WatchOutcome(-1));
        var reparsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "manifest.json")));
        Assert.Equal(-1, reparsed.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Empty(Directory.GetFiles(session, "manifest.json.tmp*"));
    }

    [Fact]
    public void WriteManifest_UnknownExitStatusIsNull()
    {
        var manager = CreateManager();
        using var lease = manager.CreateSession()!;
        var session = lease.Directory;
        manager.WriteManifest(session, new WatchOutcome());
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "manifest.json")));
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Empty(new WatchOutcome().OutputLines);
    }

    [Fact]
    public void StopGeneration_IsObservedByAllExistingWatchersButNotNewOnes()
    {
        var home = Path.Combine(_installPath, "home");
        Directory.CreateDirectory(home);
        var options = WatchOptions.Parse(new[] {
            "--target-name", "TestApp", "--install-path", _installPath,
            "--version", "1.0", "--bowl-home", home
        }, out var errors);
        Assert.Empty(errors);
        var first = new OutboxManager(options!);
        var second = new OutboxManager(options!);
        var firstGeneration = first.ReadStopGeneration();
        var secondGeneration = second.ReadStopGeneration();
        OutboxManager.RequestStop(home);
        Assert.True(first.IsStopRequested(firstGeneration));
        Assert.True(second.IsStopRequested(secondGeneration));
        var newGeneration = first.ReadStopGeneration();
        Assert.False(first.IsStopRequested(newGeneration));
        OutboxManager.RequestStop(home);
        Assert.True(first.IsStopRequested(newGeneration));
    }

    [Fact]
    public void RemoveSessionIfEmpty_DeletesEmptyDirectoryOnly()
    {
        var manager = CreateManager();

        using var emptyLease = manager.CreateSession()!;
        var empty = emptyLease.Directory;
        manager.RemoveSessionIfEmpty(empty);
        Assert.False(Directory.Exists(empty));

        using var nonEmptyLease = manager.CreateSession()!;
        var nonEmpty = nonEmptyLease.Directory;
        File.WriteAllText(Path.Combine(nonEmpty, "evidence.txt"), "keep");
        manager.RemoveSessionIfEmpty(nonEmpty);
        Assert.True(Directory.Exists(nonEmpty));
    }

    [Fact]
    public void CollectGarbage_PreservesOldPendingAndIncompleteCases()
    {
        var manager = CreateManager();
        using var oldLease = manager.CreateSession()!;
        var old = oldLease.Directory;
        File.WriteAllText(Path.Combine(old, "manifest.json"),
            $$"""{ "schema": 2, "guid": "{{Path.GetFileName(old)}}", "createdAtUtc": "{{DateTime.UtcNow.AddDays(-20):O}}", "state": "pending" }""");

        using var freshLease = manager.CreateSession()!;
        var fresh = freshLease.Directory;
        File.WriteAllText(Path.Combine(fresh, "manifest.json"),
            $$"""{ "schema": 2, "guid": "{{Path.GetFileName(fresh)}}", "createdAtUtc": "{{DateTime.UtcNow.AddHours(-1):O}}", "state": "pending" }""");

        manager.CollectGarbage();

        Assert.True(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Admission_PreservesExistingCasesAndStopsAtLimit()
    {
        var manager = CreateManager();
        var created = new List<string>();
        for (var i = 0; i < OutboxManager.MaxCasesPerVersion; i++)
        {
            using var lease = manager.CreateSession()!;
            var session = lease.Directory;
            if (i % 2 == 0) File.WriteAllText(Path.Combine(session, "manifest.json"), "corrupt");
            created.Add(session);
        }

        manager.CollectGarbage();
        Assert.Null(manager.CreateSession());
        var remaining = Directory.GetDirectories(Path.Combine(_installPath, "fail", "1.0.2"));
        Assert.Equal(OutboxManager.MaxCasesPerVersion, remaining.Length);
        Assert.All(created, item => Assert.Contains(item, remaining));
    }

    [Fact]
    public void CollectGarbage_NoFailRoot_NoThrow()
    {
        var manager = CreateManager(); // never created any session
        manager.CollectGarbage();      // must not throw
        Assert.True(true);
    }
}

/// <summary>Test helper: WatchOptions exposes private setters, so tests assemble it via parse.</summary>
internal sealed class WatchOptionsBuilder
{
    private string _installPath = string.Empty;
    private string _version = string.Empty;

    public WatchOptionsBuilder WithInstallPath(string path)
    {
        _installPath = path;
        return this;
    }

    public WatchOptionsBuilder WithVersion(string version)
    {
        _version = version;
        return this;
    }

    public WatchOptions Build()
    {
        var options = WatchOptions.Parse(new[]
        {
            "--target-name", "TestApp",
            "--install-path", _installPath,
            "--version", _version,
        }, out var errors);
        Assert.Empty(errors);
        return options!;
    }
}
