using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bowl.Host;

// Descriptor predicate, not a filesystem fence. Callers must acquire stable
// handles and check identity, links and every ancestor before relying on it.
[SupportedOSPlatform("windows")]
internal static class ControlledAcl
{
    private const uint MutatingRights = 0xD0156u | 0x10000000u | 0x40000000u | 0x02000000u;

    public static bool IsRestricted(RawSecurityDescriptor descriptor, IReadOnlySet<SecurityIdentifier> trusted)
    {
        if (descriptor.Owner == null || !trusted.Contains(descriptor.Owner) ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            descriptor.DiscretionaryAcl == null)
            return false;
        foreach (var entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.IsCallback ||
                ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                return false;
            if (ace.AceQualifier == AceQualifier.AccessAllowed &&
                ((uint)ace.AccessMask & MutatingRights) != 0 && !trusted.Contains(ace.SecurityIdentifier))
                return false;
        }
        return true;
    }
}
