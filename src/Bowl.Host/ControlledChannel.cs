using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Bowl.Host;

// Authentication primitive only; the caller must keep the returned process
// handle and abort if it exits. No installation authorization is implied.
[SupportedOSPlatform("windows")]
internal static class ControlledChannel
{
    public static Process AuthenticateClient(NamedPipeServerStream pipe, ProcessIdentity expected) =>
        Authenticate(pipe, expected, true);

    public static Process AuthenticateServer(NamedPipeClientStream pipe, ProcessIdentity expected) =>
        Authenticate(pipe, expected, false);

    private static Process Authenticate(PipeStream pipe, ProcessIdentity expected, bool server)
    {
        if (!pipe.IsConnected)
            throw new InvalidOperationException("Pipe is not connected.");
        uint pid;
        var ok = server
            ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (pid != expected.Pid)
            throw new InvalidDataException("Unexpected pipe peer.");
        var process = Process.GetProcessById(expected.Pid);
        try
        {
            if (process.HasExited ||
                !Processes.SameProcessStart(process.StartTime.ToUniversalTime(), expected.StartTimeUtc))
                throw new InvalidDataException("Pipe peer identity changed.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
