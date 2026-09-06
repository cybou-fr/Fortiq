using Fortiq.Application;

namespace Fortiq.Desktop;

/// <summary>
/// Turns the service's refusal into something a person can act on, and recognises the one refusal
/// that is not about their request at all.
/// </summary>
/// <remarks>
/// A newer desktop asking an older service for something it has never heard of is answered "Unknown
/// IPC command", which is true and names the wrong problem: nothing is wrong with the request, the two
/// halves of the installation are different builds. That is a normal state during an update and a
/// permanent one whenever a portable copy is opened on a machine that already has a service.
///
/// Pure, and separate from the client that talks over the pipe, because this is the part with a
/// decision in it. The pipe is plumbing; which refusal means "your installation is mismatched" is a
/// judgement, and it is the part worth pinning down in tests.
/// </remarks>
public static class ServiceSkewMessage
{
    private const string UnknownCommand = "Unknown IPC command";

    /// <summary>Whether <paramref name="reported"/> is the service saying it does not know the command.</summary>
    public static bool IsVersionSkew(string? reported) =>
        reported is not null && reported.Contains(UnknownCommand, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to show for a refusal.
    /// </summary>
    /// <param name="command">The command that was refused.</param>
    /// <param name="reported">What the service said, when it said anything.</param>
    /// <param name="fallback">What to say when the service gave no reason at all.</param>
    /// <param name="serviceVersion">
    /// The version the service reports, or null when it is too old to report one - which is itself
    /// consistent with the problem being described, so the sentence has to work without it.
    /// </param>
    public static string Describe(string command, string? reported, string fallback, string? serviceVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        if (!IsVersionSkew(reported))
        {
            // Everything else the service says is the most useful sentence available - an authorisation
            // denial names the account and the group and what to do about both - and is passed through
            // rather than replaced with something more general.
            return reported ?? fallback;
        }

        return $"The Fortiq service on this PC is an older build than this application, so it does not know how to "
            + $"'{command}'. This application is {FortiqVersion.Current}; the service "
            + (serviceVersion is { Length: > 0 } version ? $"is {version}" : "did not say which version it is")
            + ". Install the newer release over this one - it replaces both halves - and try again.";
    }
}
