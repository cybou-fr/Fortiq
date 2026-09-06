using System.Runtime.Versioning;
using System.Security.Principal;
using Fortiq.Platform.Windows;

namespace Fortiq.Security.Tests;

/// <summary>
/// The local group that lets somebody run Fortiq's everyday operations without administrative rights.
/// </summary>
/// <remarks>
/// These are the answers a machine gives when the group is not there, which is every installation made
/// before it existed and every machine where creating it was refused. They matter more than they look:
/// a lookup that failed open would hand the operator level to everybody, and the group is not something
/// a test suite may create on a developer's machine to find out.
///
/// Creating it for real belongs to the elevated pilot lane, where a machine is already being changed
/// on purpose.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FortiqOperatorsGroupTests
{
    [SkippableFact]
    public void AGroupThisMachineDoesNotHaveGrantsNobodyAnything()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Local groups are a Windows concern.");
        Skip.If(FortiqOperatorsGroup.Exists(), "This machine already has the group, so its absence cannot be tested here.");

        Assert.Null(FortiqOperatorsGroup.Sid());
        Assert.False(FortiqOperatorsGroup.IsCurrentUserMember());

        using var identity = WindowsIdentity.GetCurrent();
        Assert.False(FortiqOperatorsGroup.IsMember(identity));
    }

    [SkippableFact]
    public void MembershipIsAskedOfATokenRatherThanAssumedFromAName()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Local groups are a Windows concern.");

        // Whatever this machine's answer is, it must be the same one for the current identity and for
        // the current process - one of these reading a name where the other reads a token is how a
        // check ends up meaning two different things.
        using var identity = WindowsIdentity.GetCurrent();

        Assert.Equal(FortiqOperatorsGroup.IsCurrentUserMember(), FortiqOperatorsGroup.IsMember(identity));
    }

    [Fact]
    public void TheGroupSaysWhatItIsForWhereWindowsWillShowIt()
    {
        // The description is what an administrator reads in Computer Management before deciding who
        // goes in, and the one thing they must not have to guess is what it does not cover.
        Assert.Contains("Cannot protect new folders", FortiqOperatorsGroup.Description, StringComparison.Ordinal);
        Assert.Equal("Fortiq Operators", FortiqOperatorsGroup.Name);
    }
}
