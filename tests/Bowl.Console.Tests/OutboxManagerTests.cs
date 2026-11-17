using System.Text.Json;
using GeneralUpdate.Bowl.ConsoleApp;

namespace Bowl.Console.Tests;

/// <summary>
///     Outbox contract tests: schema-2 manifest writing, empty-session cleanup and the disk GC
///     rules (14-day retention, 10-cases-per-version cap, oldest-first eviction).
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
        var session = manager.CreateSessionDirectory();

        Assert.True(Directory.Exists(session));
        Assert.Equal(Path.Combine(_installPath, "fail", "1.0.2"), Path.GetDirectoryName(session));
        Assert.True(Guid.TryParseExact(Path.GetFileName(session), "D", out _));
    }

    [Fact]
    public void WriteManifest_Schema2ContractShape()
    {
        var manager = CreateManager();
        var session = manager.CreateSessionDirectory();
        File.WriteAllText(Path.Combine(session, "1.0.2_fail.json"), "{}");
        File.WriteAllText(Path.Combine(session, "sysinfo.txt"), "sys");

        manager.WriteManifest(session, new WatchOutcome(-1073741819));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("schema").GetInt32());
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
        var session = manager.CreateSessionDirectory();
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
    public void RemoveSessionIfEmpty_DeletesEmptyDirectoryOnly()
    {
        var manager = CreateManager();

        var empty = manager.CreateSessionDirectory();
        manager.RemoveSessionIfEmpty(empty);
        Assert.False(Directory.Exists(empty));

        var nonEmpty = manager.CreateSessionDirectory();
        File.WriteAllText(Path.Combine(nonEmpty, "evidence.txt"), "keep");
        manager.RemoveSessionIfEmpty(nonEmpty);
        Assert.True(Directory.Exists(nonEmpty));
    }

    [Fact]
    public void CollectGarbage_RemovesCasesBeyondRetention()
    {
        var manager = CreateManager();
        var old = manager.CreateSessionDirectory();
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "manifest.json"),
            $$"""{ "schema": 2, "guid": "{{Path.GetFileName(old)}}", "createdAtUtc": "{{DateTime.UtcNow.AddDays(-20):O}}", "state": "pending" }""");

        var fresh = manager.CreateSessionDirectory();
        File.WriteAllText(Path.Combine(fresh, "manifest.json"),
            $$"""{ "schema": 2, "guid": "{{Path.GetFileName(fresh)}}", "createdAtUtc": "{{DateTime.UtcNow.AddHours(-1):O}}", "state": "pending" }""");

        manager.CollectGarbage();

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void CollectGarbage_KeepsAtMostTenNewestCases()
    {
        var manager = CreateManager();
        var created = new List<string>();
        for (var i = 0; i < 13; i++)
        {
            var session = manager.CreateSessionDirectory();
            // Distinct manifest timestamps enforce a deterministic oldest-first order.
            File.WriteAllText(Path.Combine(session, "manifest.json"),
                $$"""{ "schema": 2, "createdAtUtc": "{{DateTime.UtcNow.AddMinutes(-100 + i):O}}" }""");
            created.Add(session);
        }

        manager.CollectGarbage();

        var remaining = Directory.GetDirectories(Path.Combine(_installPath, "fail", "1.0.2"));
        Assert.Equal(10, remaining.Length);
        // The three oldest (lowest timestamps) were evicted.
        Assert.DoesNotContain(created[0], remaining);
        Assert.DoesNotContain(created[1], remaining);
        Assert.DoesNotContain(created[2], remaining);
        Assert.Contains(created[12], remaining);
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
