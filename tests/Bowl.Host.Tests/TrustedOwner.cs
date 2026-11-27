using System.Security.AccessControl;
using System.Security.Principal;

namespace Bowl.Host.Tests;

// Elevated processes (CI runners, administrators) create filesystem objects
// owned by BUILTIN\Administrators instead of the interacting user. The
// controlled-tree tests model trees owned by the current user, so every
// entry a test creates must be forced back to that owner — otherwise the
// owner trust predicate rejects the tree before any DACL check runs.
internal static class TrustedOwner
{
    public static void Apply(string path, SecurityIdentifier user)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.SetOwner(user);
            info.SetAccessControl(security);
        }
        else if (File.Exists(path))
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetOwner(user);
            info.SetAccessControl(security);
        }
    }
}
