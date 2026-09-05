namespace Fortiq.Service;

/// <summary>What a caller has to be in order to issue a command.</summary>
public enum ServiceIpcCommandTrust
{
    /// <summary>Any account that can open the pipe may ask. The answer discloses nothing privileged.</summary>
    Public,

    /// <summary>The command acts with the service's privileges, so the caller must hold them too.</summary>
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
/// they are not exercising it. A local <c>Fortiq Operators</c> group is the natural place to widen this
/// later; it is deliberately not here yet, because a group nobody has created yet would be a second
/// code path that never runs.
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
        _ => ServiceIpcCommandTrust.Privileged
    };

    /// <summary>Decides whether a caller may issue <paramref name="command"/>.</summary>
    /// <param name="command">The command asked for.</param>
    /// <param name="callerIsAdministrator">
    /// Whether the caller's own token carries Administrators as an enabled group.
    /// </param>
    /// <param name="callerAccountName">The caller's account, named in the refusal so it can be traced.</param>
    public static ServiceIpcAuthorizationResult Authorize(
        string? command,
        bool callerIsAdministrator,
        string callerAccountName)
    {
        if (TrustFor(command) == ServiceIpcCommandTrust.Public)
        {
            return ServiceIpcAuthorizationResult.Allow;
        }

        if (callerIsAdministrator)
        {
            return ServiceIpcAuthorizationResult.Allow;
        }

        return ServiceIpcAuthorizationResult.Deny(
            $"'{command}' acts with the Fortiq service's privileges and '{callerAccountName}' does not hold them. " +
            "Run the request from an elevated session.");
    }
}
