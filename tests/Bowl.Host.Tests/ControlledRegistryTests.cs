using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ControlledRegistryTests
{
    [Fact]
    public void RegistryRejectsAlternateStateDependentRoots()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixedRoot = ControlledRegistry.FixedRoot();
        ControlledRegistry.ValidateRoot(fixedRoot);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows())
                ControlledRegistry.ValidateRoot(Path.Combine(Path.GetTempPath(), "alternate-bowl-registry"));
        });
    }
}
