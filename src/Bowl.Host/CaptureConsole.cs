using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bowl.Host;

internal static class CaptureConsole
{
    public static void Stop(int pid)
    {
        if (!FreeConsole() || !AttachConsole((uint)pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetConsoleCtrlHandler(IntPtr.Zero, true) || !GenerateConsoleCtrlEvent(0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
}
