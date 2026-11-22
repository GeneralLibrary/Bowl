using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bowl.Host;

// configPath is supplied by the service installer, not by an IPC request.
[SupportedOSPlatform("windows")]
internal static class ControlledConfiguration
{
    public static ControlledSettings Load(string configPath)
    {
        configPath = Paths.Absolute(configPath);
        var trusted = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier))
        };
        using var configTree = ControlledTree.HoldTree(Path.GetDirectoryName(configPath)!, trusted);
        using var file = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 64 * 1024)
            throw new InvalidDataException("Controlled service configuration is too large.");
        var settings = JsonSerializer.Deserialize(file, ControlledSettingsJson.Default.ControlledSettings)
            ?? throw new InvalidDataException("Controlled service configuration is missing.");
        var identity = new SecurityIdentifier(settings.ServiceAccountSid);
        if (!ControlledIdentity.IsDedicatedService(identity))
            throw new InvalidDataException("Dedicated service identity is required.");
        var install = Paths.Absolute(settings.InstallationRoot);
        var registry = Paths.Absolute(settings.RegistryRoot);
        ControlledRegistry.ValidateRoot(registry);
        var state = Paths.Absolute(settings.StateRoot);
        var worker = Paths.Absolute(settings.WorkerExecutable);
        if (Paths.Overlaps(install, registry) || Paths.Overlaps(install, state) ||
            Paths.Overlaps(registry, state) || Paths.Contains(install, worker))
            throw new InvalidDataException("Controlled service paths overlap.");
        return settings;
    }
}

internal sealed record ControlledSettings
{
    public required string ServiceAccountSid { get; init; }
    public required string InstallationRoot { get; init; }
    public required string RegistryRoot { get; init; }
    public required string StateRoot { get; init; }
    public required string WorkerExecutable { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ControlledSettings))]
internal partial class ControlledSettingsJson : JsonSerializerContext;
