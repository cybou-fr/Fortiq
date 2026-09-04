using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The wizard that creates a protected repository. Its reason for existing is the rule it enforces:
/// it does not finish until the person has shown they can reproduce the recovery material.
/// </summary>
public sealed class ProtectRepositoryViewModelTests
{
    private const string Mnemonic = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima";

    [Fact]
    public async Task TheWizardWillNotStartWithoutBeingToldWhatToProtect()
    {
        var model = Wizard();
        Assert.False(model.CanCreate);

        model.RepositoryLocation = "C:/repository";
        model.KitDirectory = "C:/kit";
        Assert.False(model.CanCreate);

        model.SourcePath = "C:/documents";
        Assert.True(model.CanCreate);

        await model.CreateAsync(CancellationToken.None);
        Assert.Equal(ProtectStep.WriteDownRecoveryMaterial, model.Step);
    }

    [Fact]
    public async Task TheMnemonicIsShownOnceAndOnlyWhileItIsBeingWrittenDown()
    {
        var model = await StartedWizardAsync();

        Assert.Equal(Mnemonic, model.RecoveryMnemonic);

        model.WroteItDown();
        Assert.Null(model.RecoveryMnemonic);

        // Someone who did not manage to copy it can ask for it again - the alternative is a person
        // who clicks on and loses the repository.
        model.ShowItAgain();
        Assert.Equal(Mnemonic, model.RecoveryMnemonic);
    }

    [Fact]
    public async Task TheWizardDoesNotFinishUntilTheWordsAreTypedBackCorrectly()
    {
        var model = await StartedWizardAsync();
        model.WroteItDown();

        model.ConfirmationInput = "wrong words entirely";
        Assert.False(model.Confirm());
        Assert.Equal(ProtectStep.ConfirmRecoveryMaterial, model.Step);
        Assert.NotNull(model.Failure);

        model.ConfirmationInput = string.Join(' ', model.RequestedWordNumbers.Select(number => Words()[number - 1]));
        Assert.True(model.Confirm());
        Assert.Equal(ProtectStep.Done, model.Step);
    }

    [Fact]
    public async Task OnceConfirmedTheMnemonicIsGoneFromTheApplication()
    {
        var model = await StartedWizardAsync();
        model.WroteItDown();
        model.ConfirmationInput = string.Join(' ', model.RequestedWordNumbers.Select(number => Words()[number - 1]));
        model.Confirm();

        // Fortiq cannot produce it again, and neither can this screen: from here the written copy is
        // the only one.
        Assert.Null(model.RecoveryMnemonic);
        Assert.Equal(ProtectStep.Done, model.Step);
    }

    [Fact]
    public async Task TheWordsAskedForAreSpreadAcrossTheMnemonicRatherThanAlwaysTheFirstOnes()
    {
        var model = await StartedWizardAsync();
        model.WroteItDown();

        Assert.Equal(3, model.RequestedWordNumbers.Count);
        Assert.Equal(model.RequestedWordNumbers.Count, model.RequestedWordNumbers.Distinct().Count());
        Assert.All(model.RequestedWordNumbers, number => Assert.InRange(number, 1, Words().Length));
        Assert.Equal(model.RequestedWordNumbers.OrderBy(number => number), model.RequestedWordNumbers);
    }

    [Fact]
    public async Task AFailureToCreateIsShownRatherThanLeavingAHalfFinishedWizard()
    {
        var model = new ProtectRepositoryViewModel(new FailingCreator("the repository directory must be empty"))
        {
            RepositoryLocation = "C:/repository",
            KitDirectory = "C:/kit",
            SourcePath = "C:/documents"
        };

        await model.CreateAsync(CancellationToken.None);

        Assert.Equal(ProtectStep.Describe, model.Step);
        Assert.Equal("the repository directory must be empty", model.Failure);
        Assert.Null(model.RecoveryMnemonic);
    }

    private static string[] Words() => Mnemonic.Split(' ');

    private static ProtectRepositoryViewModel Wizard() =>
        // A fixed choice of words keeps the test about the rule rather than about chance.
        new(new FakeCreator(), (count, already) => (already * 4) % count);

    private static async Task<ProtectRepositoryViewModel> StartedWizardAsync()
    {
        var model = Wizard();
        model.RepositoryLocation = "C:/repository";
        model.KitDirectory = "C:/kit";
        model.SourcePath = "C:/documents";
        await model.CreateAsync(CancellationToken.None);
        return model;
    }

    private sealed class FakeCreator : IProtectRepository
    {
        public Task<ProtectedRepositoryResult> CreateAsync(ProtectRepositoryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProtectedRepositoryResult(new string('a', 64), Mnemonic, DeviceUnlockAvailable: true));
    }

    private sealed class FailingCreator(string message) : IProtectRepository
    {
        public Task<ProtectedRepositoryResult> CreateAsync(ProtectRepositoryRequest request, CancellationToken cancellationToken) =>
            Task.FromException<ProtectedRepositoryResult>(new InvalidOperationException(message));
    }
}
