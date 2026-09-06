using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The command line that asks an instance to perform one privileged operation and exit. What it
/// refuses matters as much as what it accepts: this decides what a raised process does.
/// </summary>
public sealed class ElevatedOperationArgumentsTests
{
    [Theory]
    [InlineData("--backup", ElevatedOperation.Backup)]
    [InlineData("--BACKUP", ElevatedOperation.Backup)]
    [InlineData("--prove", ElevatedOperation.Prove)]
    public void AnOperationAndItsRepositoryAreRead(string switchName, ElevatedOperation expected)
    {
        Assert.True(ElevatedOperationArguments.TryParse([switchName, "repo-1"], out var operation, out var repository));

        Assert.Equal(expected, operation);
        Assert.Equal("repo-1", repository);
    }

    [Fact]
    public void AnOrdinaryLaunchAsksForNoOperation()
    {
        Assert.False(ElevatedOperationArguments.TryParse(["--portable"], out _, out _));
        Assert.False(ElevatedOperationArguments.TryParse([], out _, out _));
    }

    [Fact]
    public void ASwitchWithNothingAfterItNamesNoRepository()
    {
        // Rather than operating on the empty string, or on whichever repository came first.
        Assert.False(ElevatedOperationArguments.TryParse(["--backup"], out _, out _));
        Assert.False(ElevatedOperationArguments.TryParse(["--backup", "--tray"], out _, out _));
        Assert.False(ElevatedOperationArguments.TryParse(["--prove", "   "], out _, out _));
    }

    [Fact]
    public void TheOperationIsFoundWhereverItAppears()
    {
        Assert.True(ElevatedOperationArguments.TryParse(["--tray", "--prove", "repo-2"], out var operation, out var repository));

        Assert.Equal(ElevatedOperation.Prove, operation);
        Assert.Equal("repo-2", repository);
    }
}
