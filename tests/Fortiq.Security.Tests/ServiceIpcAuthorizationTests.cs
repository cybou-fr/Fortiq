using Fortiq.Platform.Windows;
using Fortiq.Service;

namespace Fortiq.Security.Tests;

/// <summary>
/// The privilege boundary between a caller and a service running as LocalSystem.
/// </summary>
/// <remarks>
/// The attack this closes is not a bug in one command. `provision` makes the service read a source
/// path, create a machine-scoped TPM key and hand back the recovery mnemonic. With no check, any
/// account that could open the pipe could ask LocalSystem to back up a directory that account cannot
/// read, into a repository that account controls, and be given the key to it.
///
/// That command is why the whole surface was once administrator-only, and it is why the line is now
/// drawn in two places rather than one. Everything else acts on a source an administrator has already
/// protected, cannot be repointed at another path, and hands nothing back - so it is delegated to a
/// local group, and provision is not.
/// </remarks>
public sealed class ServiceIpcAuthorizationTests
{
    private const string Alice = "MACHINE\alice";

    [Theory]
    [InlineData("ping")]
    [InlineData("status")]
    [InlineData("STATUS")]
    public void StatusCommandsAreOpenToAnyCallerThatCanOpenThePipe(string command)
    {
        // The desktop runs unelevated and has to be able to show whether the service is alive. This is
        // why the boundary is drawn per command rather than by the pipe's own access control.
        Assert.True(Authorize(command, administrator: false, operatorMember: false).Allowed);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("prove")]
    [InlineData("updateSchedule")]
    [InlineData("removeSchedule")]
    [InlineData("clearLock")]
    [InlineData("BACKUP")]
    public void EverydayOperationsAreOpenToAnOperator(string command)
    {
        Assert.Equal(ServiceIpcCommandTrust.Operator, ServiceIpcAuthorization.TrustFor(command));
        Assert.True(Authorize(command, administrator: false, operatorMember: true).Allowed);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("prove")]
    [InlineData("updateSchedule")]
    [InlineData("removeSchedule")]
    [InlineData("clearLock")]
    public void EverydayOperationsAreStillRefusedToSomebodyInNeitherGroup(string command)
    {
        var result = Authorize(command, administrator: false, operatorMember: false);

        Assert.False(result.Allowed);
        Assert.Contains(Alice, result.Denial!, StringComparison.Ordinal);
        Assert.Contains(FortiqOperatorsGroup.Name, result.Denial!, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningIsNotDelegatedToOperators()
    {
        // The one command that makes LocalSystem read a path of the caller's choosing and hands back
        // the phrase that opens the result. Membership of the group must never be a way to reach it -
        // if it were, the group would be administrators under another name.
        Assert.Equal(ServiceIpcCommandTrust.Privileged, ServiceIpcAuthorization.TrustFor("provision"));

        var result = Authorize("provision", administrator: false, operatorMember: true);

        Assert.False(result.Allowed);
        Assert.Contains("not enough", result.Denial!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("provision")]
    [InlineData("backup")]
    [InlineData("prove")]
    [InlineData("clearLock")]
    public void AnAdministratorMayDoAnythingAnOperatorMay(string command)
    {
        Assert.True(Authorize(command, administrator: true, operatorMember: false).Allowed);
    }

    [Theory]
    [InlineData("retention")]
    [InlineData("credentials")]
    [InlineData("schedule")]
    [InlineData("")]
    [InlineData(null)]
    public void ACommandNobodyClassifiedIsTreatedAsPrivileged(string? command)
    {
        // A command added to the dispatch switch and forgotten here must fail closed, and closed now
        // means administrators rather than operators: the cost of that mistake is somebody being told
        // to elevate, and the cost of guessing the other way is the boundary this class exists to hold.
        Assert.Equal(ServiceIpcCommandTrust.Privileged, ServiceIpcAuthorization.TrustFor(command));
        Assert.False(Authorize(command, administrator: false, operatorMember: true).Allowed);
    }

    [Fact]
    public void ARefusalSaysBothWaysThroughIt()
    {
        var result = Authorize("backup", administrator: false, operatorMember: false);

        Assert.Contains("elevated", result.Denial!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add the account to that group", result.Denial!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMachineWithoutTheGroupBehavesExactlyAsItDidBefore()
    {
        // Every installation made before the group existed reports no membership, so the administrator
        // check is the only way through - which is where this boundary started.
        foreach (var command in new[] { "provision", "backup", "prove", "clearLock" })
        {
            Assert.False(Authorize(command, administrator: false, operatorMember: false).Allowed);
            Assert.True(Authorize(command, administrator: true, operatorMember: false).Allowed);
        }
    }

    private static ServiceIpcAuthorizationResult Authorize(string? command, bool administrator, bool operatorMember) =>
        ServiceIpcAuthorization.Authorize(command, administrator, operatorMember, Alice);
}
