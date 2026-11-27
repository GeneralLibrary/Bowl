namespace Bowl.Host;

internal sealed class RecoveryPlanException(string message) : Exception(message);

internal static class Recovery
{
    // Guarded stub: the controlled recovery engine is not wired yet, and rolling
    // back without its verified plan would overwrite concurrent user changes
    // (ProcessTests) or race a live updater. Fail closed with an explicit plan
    // exception instead of claiming a restore.
    public static void Restore(string backupDirectory, string installPath, RollbackPlan? plan) =>
        throw new RecoveryPlanException("Rollback requires the controlled recovery engine (not wired yet).");

    // Pre-flight guard: callers must invoke this before stopping the identified
    // application, so a refused rollback leaves every process untouched.
    public static void EnsureRollbackPossible() =>
        throw new RecoveryPlanException("Rollback requires the controlled recovery engine (not wired yet).");
}
