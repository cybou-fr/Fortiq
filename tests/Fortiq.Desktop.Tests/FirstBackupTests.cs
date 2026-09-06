using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The end of the protection wizard, which used to congratulate somebody on protection that did not
/// exist yet.
/// </summary>
/// <remarks>
/// It said "Protection is ready" the moment the recovery words were confirmed. At that moment the
/// repository is empty: the schedule exists and its first run is hours away, so a disk that failed
/// that night would take everything with it. The product already disagreed - a repository with no
/// backup is `never-backed-up`, which the dashboard reports as At risk - so the wizard's last word
/// and the first screen behind it said opposite things.
/// </remarks>
public sealed class FirstBackupTests
{
    [Fact]
    public async Task NothingIsBackedUpUntilSomebodyBacksItUp()
    {
        var model = await ConfirmedAsync(new FakeBackup(true));

        Assert.Equal(FirstBackupState.NotStarted, model.FirstBackup);
        Assert.True(model.CanBackUpNow);
    }

    [Fact]
    public async Task TakingTheFirstBackupIsWhatMakesTheFolderProtected()
    {
        var backup = new FakeBackup(true);
        var model = await ConfirmedAsync(backup);

        await model.RunFirstBackupAsync(CancellationToken.None);

        Assert.Equal(FirstBackupState.Done, model.FirstBackup);
        Assert.Equal("repo-1", backup.LastRepositoryId);
        Assert.Null(model.FirstBackupFailure);
    }

    [Fact]
    public async Task AFirstBackupThatFailedSaysSoAndCanBeTriedAgain()
    {
        var model = await ConfirmedAsync(new FakeBackup(false, "the backup location could not be reached"));

        await model.RunFirstBackupAsync(CancellationToken.None);

        Assert.Equal(FirstBackupState.Failed, model.FirstBackup);
        Assert.Equal("the backup location could not be reached", model.FirstBackupFailure);
    }

    [Fact]
    public async Task AThrownFailureIsShownInWordsRatherThanAsAnException()
    {
        var model = await ConfirmedAsync(new ThrowingBackup());

        await model.RunFirstBackupAsync(CancellationToken.None);

        Assert.Equal(FirstBackupState.Failed, model.FirstBackup);
        Assert.NotNull(model.FirstBackupFailure);
        Assert.DoesNotContain("Exception", model.FirstBackupFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWizardWithNoWayToBackUpDoesNotOfferTo()
    {
        // Rather than a button that fails. Where the application cannot take a backup, the screen says
        // one has not happened and leaves it there.
        var model = await ConfirmedAsync(backup: null);

        Assert.False(model.CanBackUpNow);
        await model.RunFirstBackupAsync(CancellationToken.None);
        Assert.Equal(FirstBackupState.NotStarted, model.FirstBackup);
    }

    [Fact]
    public async Task AsecondRequestWhileOneIsRunningIsIgnored()
    {
        var backup = new BlockingBackup();
        var model = await ConfirmedAsync(backup);

        var running = model.RunFirstBackupAsync(CancellationToken.None);
        await backup.Started;

        await model.RunFirstBackupAsync(CancellationToken.None);
        Assert.Equal(1, backup.Calls);

        backup.Finish();
        await running;
        Assert.Equal(FirstBackupState.Done, model.FirstBackup);
    }

    /// <summary>Drives the wizard to the screen this is about: repository made, words confirmed.</summary>
    private static async Task<ProtectRepositoryViewModel> ConfirmedAsync(IBackupNow? backup)
    {
        var model = new ProtectRepositoryViewModel(
            new FakeCreator(),
            pickWord: (_, index) => index,
            backup: backup)
        {
            RepositoryLocation = @"D:\Fortiq\repository",
            KitDirectory = @"D:\Fortiq\kit",
            SourcePath = @"C:\Users\anna\Documents"
        };

        await model.CreateAsync(CancellationToken.None);
        model.WroteItDown();
        model.ConfirmationInput = string.Join(' ', model.RequestedWordNumbers.Select(number => Words()[number - 1]));

        Assert.True(model.Confirm());
        Assert.Equal(ProtectStep.Done, model.Step);
        return model;
    }

    private static string[] Words() =>
        ("abandon ability able about above absent absorb abstract absurd abuse access accident "
        + "account accuse achieve acid acoustic acquire across act action actor actress actual").Split(' ');

    private sealed class FakeCreator : IProtectRepository
    {
        public Task<ProtectedRepositoryResult> CreateAsync(ProtectRepositoryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProtectedRepositoryResult("repo-1", string.Join(' ', Words()), true, true));

        public Task ConfirmRecoveryPhraseAsync(string repositoryId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeBackup(bool succeeds, string? failure = null) : IBackupNow
    {
        internal string? LastRepositoryId { get; private set; }

        public Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken)
        {
            LastRepositoryId = repositoryId;
            return Task.FromResult(new BackupNowResult(succeeds, succeeds ? "snap-1" : null, failure));
        }
    }

    private sealed class ThrowingBackup : IBackupNow
    {
        public Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("Access to the path is denied.");
    }

    private sealed class BlockingBackup : IBackupNow
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal int Calls { get; private set; }

        internal void Finish() => _release.TrySetResult();

        public async Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken)
        {
            Calls++;
            _started.TrySetResult();
            await _release.Task;
            return new BackupNowResult(true, "snap-1");
        }
    }
}
