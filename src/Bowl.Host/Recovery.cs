namespace Bowl.Host;

internal sealed class RecoveryPlanException(string message) : Exception(message);

internal static class Recovery
{
    public static void EnsureSafetyAvailable() =>
        throw new RecoveryPlanException("Rollback requires a verified exclusive installation fence.");

    public static void Restore(string backupDirectory, string installPath, RollbackPlan? plan) =>
        EnsureSafetyAvailable();
}
