using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GeneralUpdate.Bowl.ConsoleApp;

/// <summary>
///     Single-instance admission for one watch target. The lock key is a contract shared with
///     GeneralUpdate.Core's <c>BowlLauncher</c> (Core only PROBES the lock, never creates it):
///     <c>SHA-256("GeneralUpdate.Bowl.v2|{target}|{installPath}")</c> truncated to 32 hex chars,
///     case-folded on Windows only.
/// </summary>
/// <remarks>
///     <para>
///         Windows: named mutex <c>Global\GeneralUpdate.Bowl.{key}</c>; Unix: exclusive lock file
///         <c>{TempPath}/GeneralUpdate/bowl-locks/{key}.lock</c>. A duplicate start detects the
///         held lock and exits with <see cref="ExitCodes.AlreadyRunning"/> — starting twice is
///         harmless by construction.
///     </para>
///     <para>
///         Locks are never deleted to "steal" ownership: an abandoned mutex self-heals, a stale
///         lock file is replaced by a fresh exclusive open on the next real start.
///     </para>
/// </remarks>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;

    private SingleInstanceLock(Mutex? mutex, FileStream? lockFile)
    {
        _mutex = mutex;
        _lockFile = lockFile;
    }

    /// <summary>
    ///     Tries to acquire the lock for a target. Returns the held lock, or <c>null</c> when
    ///     another watchdog instance already owns it.
    /// </summary>
    public static SingleInstanceLock? Acquire(string targetName, string installPath)
    {
        var key = BuildKey(targetName, installPath);

        if (RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var mutex = new Mutex(initiallyOwned: true, $"Global\\GeneralUpdate.Bowl.{key}", out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstanceLock(mutex, null);
        }

        var lockPath = GetUnixLockPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null; // Held by a live watchdog.
        }

        // Record the owning attempt for operator inspection (best effort, never fatal).
        // Written directly on the stream — a StreamWriter would dispose (close) it and
        // release the lock we just acquired.
        try
        {
            var record = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} started={DateTime.UtcNow:O}\n");
            stream.Write(record, 0, record.Length);
            stream.Flush();
        }
        catch
        {
            // The lock itself is what matters; a failed sidecar write is harmless.
        }

        return new SingleInstanceLock(null, stream);
    }

    /// <summary>
     ///     Deterministic single-instance key — MUST stay byte-identical to Core's
     ///     <c>BowlLauncher.BuildInstanceKey</c> (contract row "单实例锁" in docs/bowl-redesign.md §5).
    /// </summary>
    public static string BuildKey(string targetProcessName, string installPath)
    {
        var ignoreCase = RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var normalizedPath = Path.GetFullPath(installPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (ignoreCase) normalizedPath = normalizedPath.ToLowerInvariant();
        var target = ignoreCase ? targetProcessName.Trim().ToLowerInvariant() : targetProcessName.Trim();

        var payload = $"GeneralUpdate.Bowl.v2|{target}|{normalizedPath}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            hex.Append(b.ToString("x2"));
        return hex.ToString(0, 32);
    }

    /// <summary>Well-known Unix lock file location (mirrored by Core's probe).</summary>
    public static string GetUnixLockPath(string key) =>
        Path.Combine(Path.GetTempPath(), "GeneralUpdate", "bowl-locks", $"{key}.lock");

    public void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Release on a thread that does not own the mutex — nothing to do.
        }

        _mutex?.Dispose();
        _lockFile?.Dispose();
    }
}
