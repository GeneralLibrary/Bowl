using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Bowl.Host;

// v1 is a disk contract, not a shared GeneralUpdate assembly dependency.
internal record Envelope
{
    [JsonRequired]
    public int ProtocolVersion { get; init; } = 1;
    public required string AttemptId { get; init; }
}

internal sealed record ProcessIdentity(int Pid, DateTimeOffset StartTimeUtc)
{
    public static ProcessIdentity Current()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime());
    }
}

internal sealed record Request : Envelope
{
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required ProcessIdentity Updater { get; init; }
    public required string InstallPath { get; init; }
    public string? BackupDirectory { get; init; }
    public string? CurrentVersion { get; init; }
    public string? TargetVersion { get; init; }
    public required string LaunchMode { get; init; }
    public int HealthTimeoutSeconds { get; init; } = 5;
    public int UpdateTimeoutSeconds { get; init; } = 600;
    public bool AutoRollback { get; init; }
    public ReportConfiguration Report { get; init; } = new();
}

internal sealed record ReportConfiguration
{
    public string? Url { get; init; }
    public int RecordId { get; init; }
    public int Type { get; init; } = 1;
    public string? CredentialEnvironmentVariable { get; init; }
}

internal sealed record Ready : Envelope
{
    public required ProcessIdentity Host { get; init; }
}

internal sealed record Producer : Envelope
{
    public required string Stage { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public ProcessIdentity? Application { get; init; }
    public Failure? Error { get; init; }
}

internal sealed record Failure
{
    public required string Category { get; init; }
    public string? Stage { get; init; }
    public string? ExceptionType { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public int? HResult { get; init; }
    public string? FailedPath { get; init; }
}

// Result is the recovery journal as well as the final outcome. A deferred
// rollback is not final for reporting; restarting the attempt resumes it.
internal sealed record Result : Envelope
{
    public required string EventId { get; init; }
    public required ProcessIdentity Host { get; init; }
    public required ProcessIdentity Updater { get; init; }
    public ProcessIdentity? Application { get; init; }
    public required string Outcome { get; init; }
    public required string Stage { get; init; }
    public required string Verification { get; init; }
    public string? CurrentVersion { get; init; }
    public string? TargetVersion { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public Failure? Error { get; init; }
    public string Rollback { get; init; } = "notRequested";
    public string? RollbackError { get; init; }
    public string Diagnostics { get; init; } = "metadataOnly";
}

internal sealed record LegacyPayload(int RecordId, int Status, int Type);

internal sealed record Outbox : Envelope
{
    public required string EventId { get; init; }
    public required LegacyPayload Payload { get; init; }
    public string State { get; init; } = "pending";
    public int Attempts { get; init; }
    public DateTimeOffset NextAttemptUtc { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
    public string? LastError { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Ready))]
[JsonSerializable(typeof(Producer))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(Outbox))]
[JsonSerializable(typeof(LegacyPayload))]
internal partial class ProtocolJson : JsonSerializerContext;
