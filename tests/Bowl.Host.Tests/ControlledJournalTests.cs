using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ControlledJournalTests
{
    [Fact]
    public void WrittenIntentSurvivesReopenAndCannotSkipOrReverseStates()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        var hash = new string('a', 64);
        ControlledJournal.Create(path, f.Request.AttemptId, [new("file.txt", hash, hash)]);
        Assert.Throws<InvalidDataException>(() => ControlledJournal.Advance(path, f.Request.AttemptId, 0, "complete"));
        ControlledJournal.Advance(path, f.Request.AttemptId, 0, "inProgress");
        Assert.Equal("inProgress", Disk.Read(path, JournalJson.Default.JournalRecord)!.Entries[0].State);
        Assert.Throws<InvalidDataException>(() => ControlledJournal.Create(path, f.Request.AttemptId, [new("file.txt", hash, hash)]));
        ControlledJournal.Advance(path, f.Request.AttemptId, 0, "complete");
        Assert.Throws<InvalidDataException>(() => ControlledJournal.Advance(path, f.Request.AttemptId, 0, "inProgress"));
    }

    [Theory]
    [InlineData("../data")]
    [InlineData("data\\..\\file")]
    [InlineData("file:stream")]
    public void InvalidPathsAreRejectedBeforeJournalCreation(string path)
    {
        using var f = new Fixture();
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        Assert.Throws<InvalidDataException>(() => ControlledJournal.Create(journal, f.Request.AttemptId,
            [new(path, null, new string('a', 64))]));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void AlternateSeparatorsCannotAliasJournalEntries()
    {
        using var f = new Fixture();
        var journal = Path.Combine(f.DirectoryPath, "controlled-journal.json");
        var hash = new string('a', 64);
        Assert.Throws<InvalidDataException>(() => ControlledJournal.Create(journal, f.Request.AttemptId,
            [new("folder/file.txt", null, hash), new("folder\\file.txt", null, hash)]));
        Assert.False(File.Exists(journal));
    }
}
