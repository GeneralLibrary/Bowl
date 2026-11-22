using System.Text.Json.Serialization;

namespace Bowl.Host;

// Prototype write-ahead record. The caller must hold a validated installation
// lease and verify the journal against the immutable producer plan.
internal static class ControlledJournal
{
    public static void Create(string path, string attempt, JournalEntry[] entries)
    {
        if (!ValidPlan(attempt, entries) || entries.Any(e => e.State != "pending"))
            throw new InvalidDataException("Invalid recovery journal plan.");
        if (Disk.Read(path, JournalJson.Default.JournalRecord) != null)
            throw new InvalidDataException("Recovery journal already exists.");
        Disk.Write(path, new JournalRecord(1, attempt, entries), JournalJson.Default.JournalRecord);
    }

    public static void Advance(string path, string attempt, int index, string state)
    {
        var journal = Read(path, attempt);
        if (index < 0 || index >= journal.Entries.Length)
            throw new InvalidDataException("Recovery journal identity mismatch.");
        var entry = journal.Entries[index];
        if ((entry.State, state) is not (("pending", "inProgress") or ("inProgress", "complete")))
            throw new InvalidDataException("Invalid recovery journal transition.");
        var entries = (JournalEntry[])journal.Entries.Clone();
        entries[index] = entry with { State = state };
        Disk.Write(path, journal with { Entries = entries }, JournalJson.Default.JournalRecord);
    }

    public static JournalRecord Read(string path, string attempt)
    {
        var journal = Disk.Read(path, JournalJson.Default.JournalRecord)
            ?? throw new InvalidDataException("Recovery journal missing.");
        if (journal.Version != 1 || journal.Attempt != attempt || !ValidPlan(attempt, journal.Entries))
            throw new InvalidDataException("Recovery journal identity or plan is invalid.");
        return journal;
    }

    private static bool ValidPlan(string attempt, JournalEntry[]? entries) =>
        Guid.TryParseExact(attempt, "D", out var id) && id.ToString("D") == attempt &&
        entries is { Length: >= 1 and <= 10_000 } &&
        entries.All(e => e != null && ValidRelative(e.Path) && ValidHash(e.UpdatedSha256) &&
            (e.OriginalSha256 == null || ValidHash(e.OriginalSha256)) &&
            e.State is "pending" or "inProgress" or "complete") &&
        entries.Select(e => e.Path.Replace('\\', '/')).Distinct(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() == entries.Length;

    private static bool ValidHash(string hash) => hash is { Length: 64 } &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidRelative(string path) => !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) && path.Split(['/', '\\']).All(part =>
            part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') &&
            !part.Contains(':') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
}

internal sealed record JournalEntry(string Path, string? OriginalSha256, string UpdatedSha256, string State = "pending");
internal sealed record JournalRecord(int Version, string Attempt, JournalEntry[] Entries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JournalRecord))]
internal partial class JournalJson : JsonSerializerContext;
