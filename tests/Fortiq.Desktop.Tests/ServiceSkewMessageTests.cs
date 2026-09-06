using Fortiq.Application;
using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// What the desktop says when the service on the machine is a different build from itself.
/// </summary>
/// <remarks>
/// Not a hypothetical state: the two halves are separate files, an update replaces them one after the
/// other, and a portable copy is routinely opened on a machine that already has a service installed.
/// Before this, a newer desktop asking an older service for something it had never heard of showed
/// the person "Unknown IPC command: 'backup'" — true, and about the wrong thing.
/// </remarks>
public sealed class ServiceSkewMessageTests
{
    [Fact]
    public void AnOlderServiceIsExplainedRatherThanQuoted()
    {
        var message = ServiceSkewMessage.Describe(
            "backup",
            "Unknown IPC command: 'backup'",
            "Service failed to run the backup.",
            serviceVersion: "0.0.9");

        Assert.Contains("older build", message, StringComparison.Ordinal);
        Assert.Contains("0.0.9", message, StringComparison.Ordinal);
        Assert.Contains(FortiqVersion.Current, message, StringComparison.Ordinal);
        Assert.Contains("Install the newer release", message, StringComparison.Ordinal);

        // The raw refusal is not what the person is shown: it names the request, and the request is fine.
        Assert.DoesNotContain("Unknown IPC command", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceTooOldToSayItsVersionIsStillExplained()
    {
        // A build old enough to answer status with the flat {"status":"ok"} it used to send reports no
        // version. The number is the nicety; the explanation is the point, and it has to survive that.
        var message = ServiceSkewMessage.Describe(
            "clearLock",
            "Unknown IPC command: 'clearLock'",
            "The service did not clear the lock.",
            serviceVersion: null);

        Assert.Contains("older build", message, StringComparison.Ordinal);
        Assert.Contains("did not say which version", message, StringComparison.Ordinal);
        Assert.Contains(FortiqVersion.Current, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalThatIsAboutTheRequestIsPassedThroughUntouched()
    {
        // Only the one refusal that is not about the request gets rewritten. An authorisation denial
        // names the account, the group and both ways through, and is the most useful sentence the
        // service produces - replacing it with something more general would be a loss.
        const string denial =
            "'backup' is run by the Fortiq service on this machine's behalf, and 'MACHINE\\alice' is neither "
            + "an administrator nor a member of the local 'Fortiq Operators' group.";

        Assert.Equal(denial, ServiceSkewMessage.Describe("backup", denial, "fallback", serviceVersion: "0.1.0"));
    }

    [Fact]
    public void ARefusalWithNoReasonAtAllFallsBackToTheCallersOwnWords()
    {
        Assert.Equal(
            "Service failed to run the backup.",
            ServiceSkewMessage.Describe("backup", null, "Service failed to run the backup.", serviceVersion: null));
    }

    [Theory]
    [InlineData("Unknown IPC command: 'backup'", true)]
    [InlineData("unknown ipc command: 'backup'", true)]
    [InlineData("No schedule found on machine for repository 'a'.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheServiceSayingItDoesNotKnowTheCommandCountsAsSkew(string? reported, bool expected)
    {
        Assert.Equal(expected, ServiceSkewMessage.IsVersionSkew(reported));
    }

    [Fact]
    public void TheVersionReportedIsTheOneEverythingElseReports()
    {
        // The desktop's number and the service's number come from the same reader, so a mismatch in
        // the message is a real mismatch between builds rather than two ways of asking.
        Assert.False(string.IsNullOrWhiteSpace(FortiqVersion.Current));
        Assert.DoesNotContain("+", FortiqVersion.Current, StringComparison.Ordinal);
    }
}
