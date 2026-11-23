using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Bowl.Host;

namespace Bowl.Host.Tests;

public sealed class ControlledTreeTests
{
    [Fact]
    public void FullTreeRejectsUntrustedAncestors()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        var nobody = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows())
                using (ControlledTree.HoldTree(f.Request.InstallPath, new HashSet<SecurityIdentifier> { nobody })) { }
        });
    }

    [Fact]
    public void RestrictedSubtreePinsEveryExistingEntry()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(f.Request.InstallPath).SetAccessControl(security);
        var nested = Directory.CreateDirectory(Path.Combine(f.Request.InstallPath, "nested")).FullName;
        new DirectoryInfo(nested).SetAccessControl(security);
        var file = Path.Combine(nested, "file.txt");
        File.WriteAllText(file, "content");
        Fixture.RestrictFile(file, user);
        using (ControlledTree.HoldSubtree(f.Request.InstallPath, new HashSet<SecurityIdentifier> { user }))
        {
            Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
            Assert.Throws<IOException>(() => Directory.Move(nested, nested + ".moved"));
        }
        File.Move(file, file + ".moved");
    }

    [Fact]
    public void MutationSubtreePinsDirectoriesButPermitsFileReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(f.Request.InstallPath).SetAccessControl(security);
        var nested = Directory.CreateDirectory(Path.Combine(f.Request.InstallPath, "nested")).FullName;
        new DirectoryInfo(nested).SetAccessControl(security);
        var target = Path.Combine(nested, "file.txt");
        var staged = Path.Combine(nested, "staged.txt");
        File.WriteAllText(target, "old");
        File.WriteAllText(staged, "new");
        Fixture.RestrictFile(target, user);
        Fixture.RestrictFile(staged, user);
        using (ControlledTree.HoldMutationSubtree(f.Request.InstallPath, new HashSet<SecurityIdentifier> { user }))
        {
            Assert.Throws<IOException>(() => Directory.Move(nested, nested + ".moved"));
            File.Replace(staged, target, null);
            Assert.Equal("new", File.ReadAllText(target));
        }
        Directory.Move(nested, nested + ".moved");
    }

    [Fact]
    public void MutationSubtreeRejectsHardLinksAndUntrustedFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(f.Request.InstallPath).SetAccessControl(security);
        var file = Path.Combine(f.Request.InstallPath, "data.txt");
        var link = Path.Combine(f.Request.InstallPath, "hardlink.txt");
        File.WriteAllText(file, "data");
        Fixture.RestrictFile(file, user);
        Assert.True(CreateHardLink(link, file, IntPtr.Zero));
        var trusted = new HashSet<SecurityIdentifier> { user };
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows()) using (ControlledTree.HoldMutationSubtree(f.Request.InstallPath, trusted)) { }
        });
        File.Delete(link);
        var fileSecurity = new FileInfo(file).GetAccessControl();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(fileSecurity);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows()) using (ControlledTree.HoldMutationSubtree(f.Request.InstallPath, trusted)) { }
        });
    }

    [Fact]
    public void RestrictedFileIsPinnedAndHardLinkIsRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        var path = Path.Combine(f.Request.InstallPath, "item.txt");
        var link = Path.Combine(f.Request.InstallPath, "hardlink.txt");
        File.WriteAllText(path, "content");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        var trusted = new HashSet<SecurityIdentifier> { user };
        using (ControlledTree.OpenValidatedPath(path, trusted))
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
        Assert.True(CreateHardLink(link, path, IntPtr.Zero));
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows())
                using (ControlledTree.OpenValidatedPath(path, trusted)) { }
        });
    }

    [Fact]
    public void WritableUntrustedSidIsRejectedOnRealFile()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        var path = Path.Combine(f.Request.InstallPath, "item.txt");
        File.WriteAllText(path, "content");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (OperatingSystem.IsWindows())
                using (ControlledTree.OpenValidatedPath(path, new HashSet<SecurityIdentifier> { user })) { }
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
