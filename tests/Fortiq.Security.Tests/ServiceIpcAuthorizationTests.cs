using Fortiq.Service;

namespace Fortiq.Security.Tests;

/// <summary>
/// The privilege boundary between an unelevated caller and a service running as LocalSystem.
/// </summary>
/// <remarks>
/// The attack this closes is not a bug in one command. `provision` makes the service read a source
/// path, create a machine-scoped TPM key and hand back the recovery mnemonic. With no check, any
/// account that could open the pipe could ask LocalSystem to back up a directory that account cannot
/// read, into a repository that account controls, and be given the key to it.
/// </remarks>
public sealed class ServiceIpcAuthorizationTests
{
    [Theory]
    [InlineData("ping")]
    [InlineData("status")]
    [InlineData("STATUS")]
    public void StatusCommandsAreOpenToAnyCallerThatCanOpenThePipe(string command)
    {
        // The desktop runs unelevated and has to be able to show whether the service is alive. This is
        // why the boundary is drawn per command rather than by the pipe's own access control.
        Assert.True(ServiceIpcAuthorization.Authorize(command, callerIsAdministrator: false, "MACHINE\\alice").Allowed);
    }

    [Theory]
    [InlineData("provision")]
    [InlineData("prove")]
    [InlineData("PROVISION")]
    public void PrivilegedCommandsAreRefusedToAnUnelevatedCaller(string command)
    {
        var result = ServiceIpcAuthorization.Authorize(command, callerIsAdministrator: false, "MACHINE\\alice");

        Assert.False(result.Allowed);
        Assert.Contains("MACHINE\\alice", result.Denial!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provision")]
    [InlineData("prove")]
    public void PrivilegedCommandsAreAllowedToAnElevatedCaller(string command)
    {
        Assert.True(ServiceIpcAuthorization.Authorize(command, callerIsAdministrator: true, "MACHINE\\admin").Allowed);
    }

    [Theory]
    [InlineData("retention")]
    [InlineData("credentials")]
    [InlineData("schedule")]
    [InlineData("")]
    [InlineData(null)]
    public void ACommandNobodyClassifiedIsTreatedAsPrivileged(string? command)
    {
        // A command added to the dispatch switch and forgotten here must fail closed. The cost of that
        // mistake is an operator being told to elevate; the cost of the opposite is the whole boundary.
        Assert.Equal(ServiceIpcCommandTrust.Privileged, ServiceIpcAuthorization.TrustFor(command));
        Assert.False(ServiceIpcAuthorization.Authorize(command, callerIsAdministrator: false, "MACHINE\\alice").Allowed);
    }

    [Fact]
    public void TheRefusalSaysWhatToDoAboutIt()
    {
        var result = ServiceIpcAuthorization.Authorize("provision", callerIsAdministrator: false, "MACHINE\\alice");

        Assert.Contains("elevated", result.Denial!, StringComparison.OrdinalIgnoreCase);
    }
}
