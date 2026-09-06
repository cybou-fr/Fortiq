using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Fortiq.Platform.Windows;

/// <summary>
/// The local group whose members may run Fortiq's everyday operations without being administrators.
/// </summary>
/// <remarks>
/// The service runs as LocalSystem and its privileged commands act with that authority, so until now
/// the only account allowed to issue one was an administrator exercising administrative rights. That
/// is a defensible line and a bad fit for what the product is: on an ordinary Windows PC with a
/// standard user account, "Protect a folder" and "Back up now" simply did not work, and the answer
/// was to run a backup client with full rights over the machine.
///
/// This group is the narrower answer, and it is deliberately narrower than the old one in both
/// directions. Its members may back up the sources an administrator has already approved, prove that
/// they restore, change when those run, and clear a lock an interrupted run left. They may not
/// provision: creating a repository makes LocalSystem read a path of the caller's choosing and hands
/// back the phrase that opens it, which is the ability to read any file on the machine, and it stays
/// with administrators.
///
/// The group is created empty. Nobody is in it until an administrator puts them there, which is the
/// point - it is a delegation somebody makes, not a default that quietly widens who can act.
/// </remarks>
public static class FortiqOperatorsGroup
{
    // The platform attribute is on the members that call Windows rather than on the type, so that
    // the name and description - which are just strings, and are quoted in messages the authorisation
    // code writes - can be used from code that is not itself Windows-only.

    /// <summary>The group's name on the local machine.</summary>
    public const string Name = "Fortiq Operators";

    /// <summary>What the group is for, as Windows shows it in Computer Management.</summary>
    public const string Description =
        "May run Fortiq backups, recovery drills and schedule changes for sources an administrator has already protected. "
        + "Cannot protect new folders.";

    private const int NERR_Success = 0;
    private const int NERR_GroupExists = 2223;
    private const int ERROR_ALIAS_EXISTS = 1379;

    /// <summary>
    /// Creates the group if this machine does not have it, and says whether it exists afterwards.
    /// </summary>
    /// <remarks>
    /// Needs administrative rights, so it belongs to installation. A failure is returned rather than
    /// thrown: a machine that could not be given the group is a machine where only administrators can
    /// act, which is exactly where Fortiq stood before this existed - a worse installation, not a
    /// failed one.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static bool TryCreate(out string? failure)
    {
        failure = null;

        if (Exists())
        {
            return true;
        }

        var info = new LOCALGROUP_INFO_1 { Name = Name, Comment = Description };
        var status = NetLocalGroupAdd(null, 1, ref info, out _);

        if (status is NERR_Success or NERR_GroupExists or ERROR_ALIAS_EXISTS)
        {
            return true;
        }

        failure = $"Windows refused to create the '{Name}' group (error {status}). "
            + "Only administrators will be able to run Fortiq's operations on this PC.";
        return false;
    }

    /// <summary>Whether this machine has the group at all.</summary>
    [SupportedOSPlatform("windows")]
    public static bool Exists() => Sid() is not null;

    /// <summary>
    /// The group's security identifier, or null when the machine does not have the group.
    /// </summary>
    /// <remarks>
    /// Resolved by name and then used as a SID, never compared by name. A name is not an identity on
    /// Windows: it can be reused after the group it belonged to is deleted, and it is what a caller
    /// on a domain-joined machine could most easily arrange to collide with.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier? Sid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, Name)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (Exception error) when (error is IdentityNotMappedException or SystemException)
        {
            // The group has not been created on this machine, which is an answer rather than a fault:
            // an installation that predates it, or one where creating it was refused.
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="identity"/> is acting as a member of the group right now.
    /// </summary>
    /// <remarks>
    /// "Right now" for the same reason the administrator check means it: a filtered token does not
    /// carry a group it has been denied, and what matters is what the caller is exercising rather than
    /// what they could become. A machine with no such group answers false, which leaves the
    /// administrator check as the only way through - the behaviour every existing installation has.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static bool IsMember(WindowsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (Sid() is not { } sid)
        {
            return false;
        }

        try
        {
            return new WindowsPrincipal(identity).IsInRole(sid);
        }
        catch (SystemException)
        {
            // A membership that cannot be established is not one that is granted.
            return false;
        }
    }

    /// <summary>Whether the account running this process is acting as a member of the group.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsCurrentUserMember()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return IsMember(identity);
        }
        catch (SystemException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOCALGROUP_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAdd(
        string? serverName,
        uint level,
        ref LOCALGROUP_INFO_1 buffer,
        out uint parameterError);
}
