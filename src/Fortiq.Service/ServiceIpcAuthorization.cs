using Fortiq.Platform.Windows;

namespace Fortiq.Service;

/// <summary>What a caller has to be in order to issue a command.</summary>
public enum ServiceIpcCommandTrust
{
    /// <summary>Any account that can open the pipe may ask. The answer discloses nothing privileged.</summary>
    Public,

    /// <summary>
    /// The command acts on a source an administrator has already protected, so a member of the local
    /// Fortiq Operators group may issue it - as may an administrator.
    /// </summary>
    /// <remarks>
    /// What bounds these is that the schedule already exists and the caller cannot change what it
    /// points at. Backing up, proving, rescheduling, clearing a lock and stopping protection all act
    /// on a source somebody with administrative rights chose; none of them can name a new path or
    /// hand back a key.
    /// </remarks>
    Operator,

    /// <summary>The command acts with the service's full privileges, so the caller must hold them.</summary>
    Privileged
}

/// <summary>Whether a caller may issue a command, and why not when they may not.</summary>
public sealed record ServiceIpcAuthorizationResult(bool Allowed, string? Denial)
{
    public static ServiceIpcAuthorizationResult Allow { get; } = new(true, null);

    public static ServiceIpcAuthorizationResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides which callers may issue which service commands.
/// </summary>
/// <remarks>
/// The service runs as LocalSystem, and its privileged commands act with that authority: <c>provision</c>
/// creates a machine-scoped TPM key, reads a source path and returns a recovery mnemonic. Without this
/// check, any account that can open the pipe can ask LocalSystem to back up a directory that account
/// cannot read, into a repository that account controls, and be handed the mnemonic that opens it.
/// That is a privilege boundary crossed by design, not a bug in one command.
///
/// The pipe's DACL cannot express this on its own, because <c>status</c> genuinely is for everyone -
/// the desktop runs unelevated and needs to show whether the service is alive. So the two live on one
/// pipe and the distinction is made here, per command.
///
/// A member of Administrators running unelevated is refused. The question is not who the caller could
/// become but what they are exercising, and an unelevated token is the operating system's answer that
/// they are not exercising it.
///
/// The local <c>Fortiq Operators</c> group is the narrower line this once said it would grow into.
/// <c>provision</c> is what forced the whole surface to be administrator-only: it makes LocalSystem
/// read a path the caller chooses and hands back the phrase that opens the result, which is the
/// ability to read any file on the machine. That one stays where it was. Everything else acts on a
/// source an administrator has already protected, cannot be repointed at a different path, and hands
/// nothing back - so it is delegated to a group somebody has to put people into, rather than requiring
/// a backup client to run with full rights over the machine.
/// </remarks>
public static class ServiceIpcAuthorization
{
    /// <summary>
    /// How much authority <paramref name="command"/> requires.
    /// </summary>
    /// <remarks>
    /// Unrecognised commands are <see cref="ServiceIpcCommandTrust.Privileged"/>. A command added to
    /// the dispatch switch and forgotten here must fail closed: the cost of that mistake is an operator
    /// being told to elevate, and the cost of the opposite is the boundary this class exists to hold.
    /// </remarks>
    public static ServiceIpcCommandTrust TrustFor(string? command) => command?.ToLowerInvariant() switch
    {
        "ping" or "status" => ServiceIpcCommandTrust.Public,

        // Everything an operator may do is here by name. Provision is deliberately absent: it falls
        // to the default below, with everything nobody has classified.
        "backup" or "prove" or "updateschedule" or "removeschedule" or "clearlock" =>
            ServiceIpcCommandTrust.Operator,

        _ => ServiceIpcCommandTrust.Privileged
    };

    /// <summary>Decides whether a caller may issue <paramref name="command"/>.</summary>
    /// <param name="command">The command asked for.</param>
    /// <param name="callerIsAdministrator">
    /// Whether the caller's own token carries Administrators as an enabled group.
    /// </param>
    /// <param name="callerIsOperator">
    /// Whether the caller's own token carries the local Fortiq Operators group as an enabled member.
    /// </param>
    /// <param name="callerAccountName">The caller's account, named in the refusal so it can be traced.</param>
    public static ServiceIpcAuthorizationResult Authorize(
        string? command,
        bool callerIsAdministrator,
        bool callerIsOperator,
        string callerAccountName)
    {
        var trust = TrustFor(command);

        if (trust == ServiceIpcCommandTrust.Public)
        {
            return ServiceIpcAuthorizationResult.Allow;
        }

        // An administrator may do anything an operator may. The reverse is the whole point of there
        // being two levels, and it is why this is not a single "is trusted" flag.
        if (callerIsAdministrator)
        {
            return ServiceIpcAuthorizationResult.Allow;
        }

        if (trust == ServiceIpcCommandTrust.Operator && callerIsOperator)
        {
            return ServiceIpcAuthorizationResult.Allow;
        }

        return trust == ServiceIpcCommandTrust.Operator
            ? ServiceIpcAuthorizationResult.Deny(
                $"'{command}' is run by the Fortiq service on this machine's behalf, and '{callerAccountName}' is " +
                $"neither an administrator nor a member of the local '{FortiqOperatorsGroup.Name}' group. " +
                "An administrator can add the account to that group, or run the request from an elevated session.")
            : ServiceIpcAuthorizationResult.Deny(
                $"'{command}' acts with the Fortiq service's full privileges and '{callerAccountName}' does not hold them. " +
                $"Membership of '{FortiqOperatorsGroup.Name}' is not enough for this one. " +
                "Run the request from an elevated session.");
    }
}
