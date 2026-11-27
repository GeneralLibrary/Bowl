using System.Runtime.Versioning;
using System.Security.Principal;

namespace Bowl.Host;

[SupportedOSPlatform("windows")]
internal static class ControlledIdentity
{
    public static bool IsDedicatedService(SecurityIdentifier expected)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User == null || !identity.User.Equals(expected) ||
            identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            identity.User.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
            identity.User.IsWellKnown(WellKnownSidType.NetworkServiceSid))
            return false;
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(new SecurityIdentifier(WellKnownSidType.ServiceSid, null));
    }
}
