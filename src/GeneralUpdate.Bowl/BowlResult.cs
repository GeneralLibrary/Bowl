namespace GeneralUpdate.Bowl;

/// <summary>Result of a Bowl surveillance run.</summary>
public readonly record struct BowlResult
{
    /// <summary>Whether the diagnostic tool completed without capturing a dump.
    /// This legacy result does not verify application startup or health.</summary>
    public bool Success { get; init; }

    /// <summary>Diagnostic tool exit code, not the application exit code.</summary>
    public int ExitCode { get; init; }

    /// <summary>Whether a dump file was captured (crash detected).</summary>
    public bool DumpCaptured { get; init; }

    /// <summary>Full path to the dump file, or <c>null</c> if no crash.</summary>
    public string? DumpFilePath { get; init; }

    /// <summary>Full path to the crash report JSON, or <c>null</c> if not generated.</summary>
    public string? CrashReportPath { get; init; }

    /// <summary>Whether the backup was restored.</summary>
    public bool Restored { get; init; }

    /// <summary>Pre-built success result.</summary>
    public static BowlResult Ok => new() { Success = true };
}
