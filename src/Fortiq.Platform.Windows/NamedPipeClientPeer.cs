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

    /// <summary>
    /// The account a connected client runs as, and whether that account is acting as an administrator
    /// on this machine right now.
    /// </summary>
    /// <param name="User">The client's account.</param>
    /// <param name="AccountName">The client's account name, for a message a person will read.</param>
    /// <param name="IsAdministrator">
    /// True when the client's token carries the Administrators group as an enabled member.
    /// </param>
    /// <param name="IsOperator">
    /// True when the client's token carries the local Fortiq Operators group as an enabled member.
    /// False on a machine that does not have the group, which is every installation made before it
    /// existed - and leaves the administrator check as the only way through, exactly as before.
    /// </param>
    public sealed record NamedPipeClientPrincipal(
        SecurityIdentifier? User,
        string AccountName,
        bool IsAdministrator,
        bool IsOperator = false);

    /// <summary>
    /// Resolves who is on the other end of <paramref name="pipe"/>, evaluated inside the client's own
    /// token rather than the server's.
    /// </summary>
    /// <remarks>
    /// Windows will not hand over the client's identity until the client has written something, so
    /// this cannot be called at connection time - only once a request has been read. A caller that
    /// needs to authorise before acting must therefore read the request first and authorise before
    /// interpreting it, which is a different thing from authorising after acting on it.
    ///
    /// A user who is a member of Administrators but is running unelevated has a filtered token, and
    /// <see cref="NamedPipeClientPrincipal.IsAdministrator"/> is false for them. That is the intended
    /// answer: the question is not who the client could become, it is what they are exercising now.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static NamedPipeClientPrincipal PrincipalOf(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        SecurityIdentifier? user = null;
        var accountName = "unknown";
        var administrator = false;
        var operatorMember = false;

        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User;
            accountName = identity.Name;
            administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            // Read from the client's own token, inside their impersonation, like the line above it.
            // Asking about the group any other way would answer a question about the service.
            operatorMember = FortiqOperatorsGroup.IsMember(identity);
        });

        return new NamedPipeClientPrincipal(user, accountName, administrator, operatorMember);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
