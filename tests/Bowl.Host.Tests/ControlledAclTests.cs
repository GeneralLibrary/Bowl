using System.Security.AccessControl;
using System.Security.Principal;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ControlledAclTests
{
    [Fact]
    public void InteractiveOrWrongIdentityCannotAssertDedicatedService()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.False(ControlledIdentity.IsDedicatedService(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
    }

    [Theory]
    [InlineData("O:SYG:SYD:(A;;FA;;;SY)(A;;FR;;;WD)", true)]
    [InlineData("O:SYG:SYD:(A;;FA;;;SY)(A;;FW;;;WD)", false)]
    [InlineData("O:SYG:SYD:(A;IO;FA;;;WD)(A;;FA;;;SY)", false)]
    [InlineData("O:WDG:SYD:(A;;FA;;;SY)", false)]
    [InlineData("O:SYG:SY", false)]
    public void OnlyTrustedOwnersAndWritersAreAllowed(string sddl, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var trusted = new HashSet<SecurityIdentifier> { new(WellKnownSidType.LocalSystemSid, null) };
        Assert.Equal(expected, ControlledAcl.IsRestricted(new RawSecurityDescriptor(sddl), trusted));
    }
}
