using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bowl.Host;

// Service installations must all use this machine-wide location; the caller
// must still validate and hold the registry's ACLs and ancestors.
[SupportedOSPlatform("windows")]
internal static class ControlledRegistry
{
    private static readonly Guid ProgramData = new("62ab5d82-fdc1-4dc3-a9dd-070d1d495d97");

    public static string FixedRoot()
    {
        var id = ProgramData;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var value);
        if (result != 0)
            Marshal.ThrowExceptionForHR(result);
        try
        {
            var path = Marshal.PtrToStringUni(value);
            if (string.IsNullOrEmpty(path))
                throw new InvalidDataException("ProgramData location is unavailable.");
            return Paths.Absolute(Path.Combine(path, "Bowl", "ControlledRegistry"));
        }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    public static void ValidateRoot(string candidate)
    {
        if (!string.Equals(Paths.Absolute(candidate), FixedRoot(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Controlled registry must use the machine-wide fixed location.");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
