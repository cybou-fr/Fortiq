using Fortiq.Application;
using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// What a person is told when something fails.
/// </summary>
public sealed class PlainFailureTests
{
    [Fact]
    public void FortiqsOwnWordsAreLeftAlone()
    {
        // These messages are written for people already. Rewriting them would lose the one thing
        // this codebase spends effort on.
        var error = new DeviceKeyIdentityException("This device key could not be opened. It is machine-scoped.");

        Assert.Equal(error.Message, PlainFailure.Describe(error));
    }

    [Fact]
    public void AWindowsRefusalIsExplainedRatherThanQuoted()
    {
        var described = PlainFailure.Describe(
            new UnauthorizedAccessException(@"Access to the path 'C:\ProgramData\Fortiq\schedules' is denied."));

        Assert.DoesNotContain(@"C:\ProgramData", described, StringComparison.Ordinal);
        Assert.Contains("administrator", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailureNobodyAnticipatedKeepsItsOwnMessage()
    {
        // An unhelpful specific beats a helpful-sounding generality: the person still has something
        // to report.
        var described = PlainFailure.Describe(new InvalidOperationException("the engine reported sequence 7 twice"));

        Assert.Equal("the engine reported sequence 7 twice", described);
    }

    [Fact]
    public void NothingAtAllStillSaysSomething()
    {
        Assert.False(string.IsNullOrWhiteSpace(PlainFailure.Describe(null)));
    }
}
