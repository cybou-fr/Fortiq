using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Fortiq.Platform.Windows;

/// <summary>
/// Identifies the client of a connected named pipe. The process handle is held while the image path
/// is read, so the process cannot exit and have its ID reused underneath the check.
/// </summary>
public static class NamedPipeClientInspector
{
    /// <summary>The executable the connected client is running. Available as soon as it connects.</summary>
    public static string ImagePathOf(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named pipe client validation is implemented for Windows only.");
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
        {
            throw new IOException("Failed to read the client process of the password pipe.", Marshal.GetLastWin32Error());
        }

        // Process holds an open handle to the process, which keeps the ID from being reused while
        // the image path below is read.
        using var client = Process.GetProcessById((int)clientProcessId);
        return client.MainModule?.FileName
            ?? throw new IOException("Failed to read the image of the password pipe client.");
    }

    /// <summary>
    /// The account the connected client runs as. Windows only allows this once data has been read
    /// from the pipe, so it cannot be checked at connection time.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier? UserOf(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        SecurityIdentifier? user = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User;
        });

        return user;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
