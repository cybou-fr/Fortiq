using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Whether anybody ever wrote down a repository's recovery phrase.
/// </summary>
/// <remarks>
/// The dangerous state is not "no backup" - it is a repository backing up perfectly whose 24 words
/// exist nowhere. The schedule is written before the words are shown, so an application killed during
/// that step leaves a source that runs nightly, passes its drills, and cannot be opened by its owner.
/// </remarks>
public sealed class RecoveryPhraseRecordTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-phrase-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AMachineThatRecordedNothingSaysNothing()
    {
        // Every repository provisioned before this record existed. Their owners may well have written
        // the words down, and calling that a failure would paint working installations red.
        Assert.Equal(RecoveryPhraseStatus.Unknown, await Record().ReadAsync("documents", CancellationToken.None));
    }

    [Fact]
    public async Task APhraseShownAndNeverConfirmedIsRememberedAsExactlyThat()
    {
        var record = Record();
        await record.IssuedAsync("documents", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(RecoveryPhraseStatus.Issued, await record.ReadAsync("documents", CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmationReplacesIssuance()
    {
        var record = Record();
        await record.IssuedAsync("documents", DateTimeOffset.UtcNow, CancellationToken.None);
        await record.ConfirmedAsync("documents", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(RecoveryPhraseStatus.Confirmed, await record.ReadAsync("documents", CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmationIsKeptEvenWhenNoIssuanceWasRecorded()
    {
        // Somebody who types the words back has demonstrably seen them, whatever the machine managed
        // to write beforehand.
        var record = Record();
        await record.ConfirmedAsync("documents", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(RecoveryPhraseStatus.Confirmed, await record.ReadAsync("documents", CancellationToken.None));
    }

    [Fact]
    public async Task OneSourcesRecordSaysNothingAboutAnother()
    {
        var record = Record();
        await record.ConfirmedAsync("documents", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(RecoveryPhraseStatus.Unknown, await record.ReadAsync("photos", CancellationToken.None));
    }

    [Fact]
    public async Task ADamagedRecordIsUnknownRatherThanAnAlarm()
    {
        // This decides whether to tell somebody their backups cannot be recovered. A corrupt small
        // file is not evidence that they never wrote their words down.
        var record = Record();
        await record.IssuedAsync("documents", DateTimeOffset.UtcNow, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_directory, "phrases", "documents.json"), "{ not json");

        Assert.Equal(RecoveryPhraseStatus.Unknown, await record.ReadAsync("documents", CancellationToken.None));
    }

    [Fact]
    public async Task AScheduleIdThatCouldEscapeTheDirectoryIsRefused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Record().ConfirmedAsync("../../windows/system32/config", DateTimeOffset.UtcNow, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RecoveryPhraseRecord Record() => new(_directory);
}
