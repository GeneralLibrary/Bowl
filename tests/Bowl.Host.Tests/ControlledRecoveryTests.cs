using System.Security.Cryptography;
using System.Text;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ControlledRecoveryTests
{
    [Theory]
    [InlineData("intent")]
    [InlineData("applied")]
    [InlineData("complete")]
    public void AtomicUpdateFailureResumesRecoveryAndPreservesUnknownFiles(string interruption)
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        var registry = Path.Combine(f.Base, "registry");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(registry);
        var install = f.Request.InstallPath;
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "old");
        File.WriteAllText(Path.Combine(install, "app.bin"), "old");
        File.WriteAllText(Path.Combine(install, "user.db"), "keep");
        var entries = new[]
        {
            new JournalEntry("app.bin", Hash("old"), Hash("new")),
            new JournalEntry("new.bin", null, Hash("added"))
        };
        using var lease = ControlledLease.Acquire(registry, install, f.Root, f.Request.AttemptId);
        ControlledJournal.Create(journal, f.Request.AttemptId, entries);
        AtomicWrite(Path.Combine(install, "app.bin"), "new", true);
        AtomicWrite(Path.Combine(install, "new.bin"), "added", false);
        Assert.Throws<InjectedFailure>(() => ControlledRecovery.Restore(install, backup, journal, f.Request.AttemptId,
            (index, phase) => { if (index == 0 && phase == interruption) throw new InjectedFailure(); }));
        ControlledRecovery.Restore(install, backup, journal, f.Request.AttemptId);
        ControlledRecovery.Restore(install, backup, journal, f.Request.AttemptId);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "app.bin")));
        Assert.False(File.Exists(Path.Combine(install, "new.bin")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(install, "user.db")));
        Assert.All(ControlledJournal.Read(journal, f.Request.AttemptId).Entries,
            entry => Assert.Equal("complete", entry.State));
    }

    [Theory]
    [InlineData("app.bin")]
    [InlineData("new.bin")]
    public void ChangedFileRefusesWholeRecoveryBeforeFirstModification(string changedFile)
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        Directory.CreateDirectory(backup);
        var install = f.Request.InstallPath;
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "old");
        File.WriteAllText(Path.Combine(install, "app.bin"), "new");
        File.WriteAllText(Path.Combine(install, "new.bin"), "added");
        ControlledJournal.Create(journal, f.Request.AttemptId,
        [
            new("app.bin", Hash("old"), Hash("new")),
            new("new.bin", null, Hash("added"))
        ]);
        File.WriteAllText(Path.Combine(install, changedFile), "user modification");
        Assert.Throws<InvalidDataException>(() =>
            ControlledRecovery.Restore(install, backup, journal, f.Request.AttemptId));
        Assert.Equal("user modification", File.ReadAllText(Path.Combine(install, changedFile)));
        Assert.Equal(changedFile == "app.bin" ? "added" : "new",
            File.ReadAllText(Path.Combine(install, changedFile == "app.bin" ? "new.bin" : "app.bin")));
        Assert.All(ControlledJournal.Read(journal, f.Request.AttemptId).Entries,
            entry => Assert.Equal("pending", entry.State));
    }

    [Fact]
    public void ChangedAfterIntentIsNotOverwrittenOnResume()
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        Directory.CreateDirectory(backup);
        var target = Path.Combine(f.Request.InstallPath, "app.bin");
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "old");
        File.WriteAllText(target, "new");
        ControlledJournal.Create(journal, f.Request.AttemptId, [new("app.bin", Hash("old"), Hash("new"))]);
        Assert.Throws<InjectedFailure>(() =>
            ControlledRecovery.Restore(f.Request.InstallPath, backup, journal, f.Request.AttemptId,
                (_, phase) => { if (phase == "intent") throw new InjectedFailure(); }));
        File.WriteAllText(target, "changed");
        Assert.Throws<InvalidDataException>(() =>
            ControlledRecovery.Restore(f.Request.InstallPath, backup, journal, f.Request.AttemptId));
        Assert.Equal("changed", File.ReadAllText(target));
        Assert.Equal("inProgress", ControlledJournal.Read(journal, f.Request.AttemptId).Entries[0].State);
    }

    [Fact]
    public void ConcurrentChangeAfterIntentIsNotOverwritten()
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        Directory.CreateDirectory(backup);
        var target = Path.Combine(f.Request.InstallPath, "app.bin");
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "old");
        File.WriteAllText(target, "new");
        ControlledJournal.Create(journal, f.Request.AttemptId, [new("app.bin", Hash("old"), Hash("new"))]);
        Assert.Throws<InvalidDataException>(() =>
            ControlledRecovery.Restore(f.Request.InstallPath, backup, journal, f.Request.AttemptId,
                (_, phase) => { if (phase == "intent") File.WriteAllText(target, "changed"); }));
        Assert.Equal("changed", File.ReadAllText(target));
    }

    [Fact]
    public void TamperedSnapshotIsRejectedBeforeAnyMutation()
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        Directory.CreateDirectory(backup);
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "tampered");
        File.WriteAllText(Path.Combine(f.Request.InstallPath, "app.bin"), "new");
        ControlledJournal.Create(journal, f.Request.AttemptId, [new("app.bin", Hash("old"), Hash("new"))]);
        Assert.Throws<InvalidDataException>(() =>
            ControlledRecovery.Restore(f.Request.InstallPath, backup, journal, f.Request.AttemptId));
        Assert.Equal("new", File.ReadAllText(Path.Combine(f.Request.InstallPath, "app.bin")));
    }

    [Fact]
    public void PartialUpdateWithNotYetCreatedFileIsRecovered()
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        Directory.CreateDirectory(backup);
        var target = Path.Combine(f.Request.InstallPath, "app.bin");
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "old");
        File.WriteAllText(target, "new");
        ControlledJournal.Create(journal, f.Request.AttemptId,
            [new("app.bin", Hash("old"), Hash("new")), new("not-written.bin", null, Hash("added"))]);
        ControlledRecovery.Restore(f.Request.InstallPath, backup, journal, f.Request.AttemptId);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(f.Request.InstallPath, "not-written.bin")));
    }

    [Fact]
    public void MissingOriginalFileIsNotSilentlyRecreated()
    {
        using var f = new Fixture();
        var backup = Path.Combine(f.Base, "snapshot");
        Directory.CreateDirectory(backup);
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        File.WriteAllText(Path.Combine(backup, "app.bin"), "old");
        ControlledJournal.Create(journal, f.Request.AttemptId, [new("app.bin", Hash("old"), Hash("new"))]);
        Assert.Throws<InvalidDataException>(() =>
            ControlledRecovery.Restore(f.Request.InstallPath, backup, journal, f.Request.AttemptId));
        Assert.False(File.Exists(Path.Combine(f.Request.InstallPath, "app.bin")));
    }

    private static void AtomicWrite(string path, string content, bool exists)
    {
        var temp = path + ".staged";
        File.WriteAllText(temp, content);
        if (exists) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed class InjectedFailure : Exception;
}
