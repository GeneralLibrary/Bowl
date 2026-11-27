using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Bowl.Host;

// Prototype primitive only: callers must independently establish a protected
// registry and canonical installation identity. Not wired to recovery.
internal sealed class ControlledLease : IDisposable
{
    private readonly FileStream ownership;
    private readonly string ownerPath;
    private readonly LeaseOwner owner;
    private readonly SafeFileHandle? installHandle;
    private bool closed;
    private bool completed;

    private ControlledLease(FileStream ownership, string ownerPath, LeaseOwner owner, SafeFileHandle? installHandle)
    {
        this.ownership = ownership;
        this.ownerPath = ownerPath;
        this.owner = owner;
        this.installHandle = installHandle;
    }

    public static ControlledLease Acquire(string registry, string install, string stateRoot, string attempt)
        => AcquireCore(registry, install, stateRoot, attempt, allowResume: true);

    // Admission must not resume an active owner until worker termination has
    // been proved by a separate crash-recovery protocol.
    public static ControlledLease AcquireFresh(string registry, string install, string stateRoot, string attempt)
        => AcquireCore(registry, install, stateRoot, attempt, allowResume: false);

    private static ControlledLease AcquireCore(
        string registry, string install, string stateRoot, string attempt, bool allowResume)
    {
        registry = Paths.Absolute(registry);
        install = Paths.Absolute(install);
        stateRoot = Paths.Absolute(stateRoot);
        if (!Guid.TryParseExact(attempt, "D", out var id) || id.ToString("D") != attempt)
            throw new InvalidDataException("Attempt must be a canonical GUID.");
        if (!Directory.Exists(registry))
            throw new InvalidDataException("Lease registry must already exist.");
        SafeFileHandle? installHandle = null;
        FileStream? ownership = null;
        try
        {
            var identity = install;
            if (OperatingSystem.IsWindows())
                (installHandle, identity) = ControlledTree.PinInstallationIdentity(install);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var lockPath = Path.Combine(registry, key + ".lock");
            var ownerPath = Path.Combine(registry, key + ".owner.json");
            Paths.NoLinks(lockPath);
            ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Disk.Read(ownerPath, LeaseJson.Default.LeaseOwner);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (current != null &&
                (current.Version != 1 || current.State is not ("active" or "complete") ||
                 !string.Equals(current.Install, identity, comparison) ||
                 string.IsNullOrWhiteSpace(current.StateRoot) ||
                 !Guid.TryParseExact(current.Attempt, "D", out var previousId) ||
                 previousId.ToString("D") != current.Attempt))
                throw new InvalidDataException("Invalid lease owner.");
            if (current?.State == "active" &&
                (!allowResume || current.Attempt != attempt ||
                 !string.Equals(current.StateRoot, stateRoot, comparison)))
                throw new InvalidDataException("An unresolved attempt owns this installation.");
            if (!allowResume && current?.Attempt == attempt)
                throw new InvalidDataException("A completed attempt cannot be admitted again.");
            var owner = new LeaseOwner(1, identity, stateRoot, attempt, "active");
            Disk.Write(ownerPath, owner, LeaseJson.Default.LeaseOwner);
            return new ControlledLease(ownership, ownerPath, owner, installHandle);
        }
        catch
        {
            ownership?.Dispose();
            installHandle?.Dispose();
            throw;
        }
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (completed)
            throw new InvalidOperationException("Lease is already complete.");
        Disk.Write(ownerPath, owner with { State = "complete" }, LeaseJson.Default.LeaseOwner);
        completed = true;
    }

    public void Dispose()
    {
        closed = true;
        ownership.Dispose();
        installHandle?.Dispose();
    }
}

internal sealed record LeaseOwner(int Version, string Install, string StateRoot, string Attempt, string State);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LeaseOwner))]
internal partial class LeaseJson : JsonSerializerContext;
