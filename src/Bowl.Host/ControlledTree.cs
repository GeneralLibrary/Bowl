using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bowl.Host;

// Prototype path pinning and descriptor inspection. The trusted SID set must
// come from protected service configuration, never from an attempt request.
[SupportedOSPlatform("windows")]
internal static class ControlledTree
{
    public static (SafeFileHandle Handle, string Identity) PinInstallationIdentity(string path)
    {
        var handle = CreateFile(path, 0x80020000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((information.Attributes & 0x10) == 0 || (information.Attributes & 0x400) != 0)
                throw new InvalidDataException("Installation must be a non-linked directory.");
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity)
            {
                if (length > 32_767)
                    throw new InvalidDataException("Installation path is too long.");
                buffer.EnsureCapacity((int)length + 1);
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
                if (length == 0 || length >= buffer.Capacity)
                    throw new InvalidDataException("Cannot resolve installation identity.");
            }
            return (handle, buffer.ToString().ToUpperInvariant());
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static IDisposable HoldTree(string root, IReadOnlySet<SecurityIdentifier> trusted)
    {
        root = Paths.Absolute(root);
        var ancestors = new Stack<string>();
        for (var parent = Path.GetDirectoryName(root); parent != null; parent = Path.GetDirectoryName(parent))
            ancestors.Push(parent);
        var held = new List<IDisposable>();
        try
        {
            while (ancestors.Count != 0)
                held.Add(OpenValidatedPath(ancestors.Pop(), trusted));
            held.Add(HoldSubtree(root, trusted));
            return new PinnedItems(held);
        }
        catch
        {
            foreach (var item in held)
                item.Dispose();
            throw;
        }
    }

    public static IDisposable HoldSubtree(string root, IReadOnlySet<SecurityIdentifier> trusted)
    {
        root = Paths.Absolute(root);
        var handles = new List<SafeFileHandle>();
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                var path = pending.Pop();
                if (handles.Count >= 10_000)
                    throw new InvalidDataException("Controlled tree exceeds the entry limit.");
                var handle = OpenValidatedPath(path, trusted);
                handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((information.Attributes & 0x10) != 0)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path))
                        pending.Push(child);
            }
            return new PinnedTree(handles);
        }
        catch
        {
            foreach (var handle in handles)
                handle.Dispose();
            throw;
        }
    }

    // Files are inspected but not pinned, allowing replacement. This only
    // works when validated ACLs and exclusive trusted-writer ownership hold.
    public static IDisposable HoldMutationTree(string root, IReadOnlySet<SecurityIdentifier> trusted)
    {
        root = Paths.Absolute(root);
        var ancestors = new Stack<string>();
        for (var parent = Path.GetDirectoryName(root); parent != null; parent = Path.GetDirectoryName(parent))
            ancestors.Push(parent);
        var held = new List<IDisposable>();
        try
        {
            while (ancestors.Count != 0)
                held.Add(OpenValidatedPath(ancestors.Pop(), trusted));
            held.Add(HoldMutationSubtree(root, trusted));
            return new PinnedItems(held);
        }
        catch
        {
            foreach (var item in held)
                item.Dispose();
            throw;
        }
    }

    public static IDisposable HoldMutationSubtree(string root, IReadOnlySet<SecurityIdentifier> trusted)
    {
        root = Paths.Absolute(root);
        var held = new List<SafeFileHandle>();
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            var count = 0;
            while (pending.Count != 0)
            {
                if (++count > 10_000)
                    throw new InvalidDataException("Controlled tree exceeds the entry limit.");
                var path = pending.Pop();
                var handle = OpenValidatedPath(path, trusted);
                try
                {
                    if (!GetFileInformationByHandle(handle, out var information))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((information.Attributes & 0x10) != 0)
                    {
                        held.Add(handle);
                        handle = null!;
                        foreach (var child in Directory.EnumerateFileSystemEntries(path))
                            pending.Push(child);
                    }
                    else if (path == root)
                        throw new InvalidDataException("Controlled tree root must be a directory.");
                }
                finally { handle?.Dispose(); }
            }
            return new PinnedTree(held);
        }
        catch
        {
            foreach (var handle in held)
                handle.Dispose();
            throw;
        }
    }

    private sealed class PinnedTree(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var handle in handles)
                handle.Dispose();
        }
    }

    private sealed class PinnedItems(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }

    public static SafeFileHandle OpenValidatedPath(string path, IReadOnlySet<SecurityIdentifier> trusted)
    {
        var handle = CreateFile(path, 0x80020000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((information.Attributes & 0x400) != 0 ||
                (information.Attributes & 0x10) == 0 && information.NumberOfLinks != 1)
                throw new InvalidDataException("Linked filesystem entry is not allowed.");
            var status = GetSecurityInfo(handle, 1, 0x5, out _, out _, out _, out _, out var descriptor);
            if (status != 0)
                throw new Win32Exception((int)status);
            try
            {
                var length = GetSecurityDescriptorLength(descriptor);
                if (length == 0 || length > 1024 * 1024)
                    throw new InvalidDataException("Invalid security descriptor.");
                var bytes = new byte[length];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var raw = new RawSecurityDescriptor(bytes, 0);
                if (!ControlledAcl.IsRestricted(raw, trusted))
                    throw new InvalidDataException(
                        $"Untrusted filesystem permissions for '{path}': {ControlledAcl.DescribeRejection(raw, trusted)}.");
            }
            finally { LocalFree(descriptor); }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
