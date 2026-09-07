using Fortiq.Application;
using Fortiq.CommunityModel;
using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Today's schedules read as tomorrow's resource model.
/// </summary>
/// <remarks>
/// What matters here is not that the projection runs but that it is honest in both directions: it
/// must lose nothing the old model knew, and it must claim nothing the old model never established.
/// A read model that quietly dropped the drill schedule, or that decided a bucket was immutable
/// because it was S3, would be worse than no read model at all - the GUI and eventually an
/// assistant will speak from this.
/// </remarks>
public sealed class LegacyScheduleProjectorTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    [Fact]
    public void NothingProjectsToAnEmptyCatalogueRatherThanNothing()
    {
        var catalogue = LegacyScheduleProjector.Project([]);

        Assert.Empty(catalogue.Tasks);
        Assert.Empty(catalogue.Sources);
        // Not even an engine: a machine protecting nothing has not chosen a repository format.
        Assert.Empty(catalogue.Engines);
    }

    [Fact]
    public void OneScheduleBecomesASourceAStorageATaskAndARoute()
    {
        var catalogue = LegacyScheduleProjector.Project([Schedule()]);

        var task = Assert.Single(catalogue.Tasks);
        var source = Assert.Single(catalogue.SourcesOf(task));
        var route = Assert.Single(catalogue.RoutesOf(task));

        Assert.Equal(@"C:\Users\anna\Documents", source.Path);
        Assert.Equal(SourceKind.Folder, source.Kind);
        Assert.NotNull(catalogue.Storage(route.StorageId));
        Assert.NotNull(catalogue.Engine(route.EngineId));
        Assert.NotNull(catalogue.EncryptionProfile(route.EncryptionProfileId));
    }

    [Fact]
    public void TheSameFolderProtectedTwiceIsOneSource()
    {
        // The first thing this model buys. Today these are two unrelated records that happen to
        // hold equal strings, and nothing in Fortiq can tell they are the same folder.
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(id: "sched-a", repository: @"E:\Backups"),
            Schedule(id: "sched-b", repository: @"s3:https://minio.example/fortiq")
        ]);

        Assert.Single(catalogue.Sources);
        Assert.Equal(2, catalogue.Tasks.Count);
        Assert.Equal(2, catalogue.Storages.Count);
    }

    [Fact]
    public void TheSameDestinationUsedTwiceIsOneStorage()
    {
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(id: "sched-a", source: @"C:\Users\anna\Documents", stableId: "src-a"),
            Schedule(id: "sched-b", source: @"C:\Users\anna\Projects", stableId: "src-b")
        ]);

        Assert.Single(catalogue.Storages);
        Assert.Equal(2, catalogue.Sources.Count);
    }

    [Fact]
    public void APhraseNobodyConfirmedIsNotARecoveryRoute()
    {
        // The claim Fortiq exists not to make. A phrase that was generated and never written down
        // protects data nobody has demonstrated they can get back, and the model has to be able to
        // say so rather than listing a recipient and implying one.
        var catalogue = LegacyScheduleProjector.Project([Schedule()]);

        var profile = Assert.Single(catalogue.EncryptionProfiles);
        Assert.False(catalogue.HasConfirmedRecipient(profile));

        var confirmed = LegacyScheduleProjector.Project(
            [Schedule()],
            [new ScheduleFacts("sched-a", RecoveryPhraseConfirmed: true)]);

        Assert.True(confirmed.HasConfirmedRecipient(Assert.Single(confirmed.EncryptionProfiles)));
    }

    [Fact]
    public void TheDeviceKeyWritesAndDoesNotRecover()
    {
        // A machine that is stolen or wiped must not be the only thing that could have opened the
        // backups, so the device key is a writer and never a recipient.
        var catalogue = LegacyScheduleProjector.Project([Schedule()]);
        var profile = Assert.Single(catalogue.EncryptionProfiles);

        var writer = catalogue.IdentityKey(Assert.Single(profile.Writers));
        var recipient = catalogue.IdentityKey(Assert.Single(profile.Recipients));

        Assert.Equal(IdentityKeyKind.DeviceBound, writer!.Kind);
        Assert.Equal(IdentityKeyKind.RecoveryPhrase, recipient!.Kind);
    }

    [Fact]
    public void AMachineWithNoDeviceKeyHasNoWriterRatherThanAPretendOne()
    {
        var catalogue = LegacyScheduleProjector.Project(
            [Schedule()],
            [new ScheduleFacts("sched-a", DeviceKeyPresent: false)]);

        Assert.Empty(Assert.Single(catalogue.EncryptionProfiles).Writers);
    }

    [Fact]
    public void EveryScheduleOnOneMachineSharesTheOneDeviceIdentity()
    {
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(id: "sched-a", stableId: "src-a", source: @"C:\A"),
            Schedule(id: "sched-b", stableId: "src-b", source: @"C:\B")
        ]);

        Assert.Single(catalogue.Identities, identity => identity.Kind == IdentityKind.Device);
        // But two phrases, because each repository has its own and they are not interchangeable.
        Assert.Equal(2, catalogue.Identities.Count(identity => identity.Kind == IdentityKind.PaperRecovery));
    }

    [Fact]
    public void TwoRepositoriesGetTwoDistinguishableRecoveryPhrases()
    {
        // Three identities all called "Recovery phrase" is three things nobody can tell apart, on
        // the screen somebody reads to find out who can get their data back. They are not
        // interchangeable, so their names must not be either. Caught by looking at the screen.
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(id: "a", stableId: "src-a", source: @"C:\Users\anna\Documents"),
            Schedule(id: "b", stableId: "src-b", source: @"C:\Users\anna\Projects")
        ]);

        var phrases = catalogue.Identities.Where(identity => identity.Kind == IdentityKind.PaperRecovery).ToList();

        Assert.Equal(2, phrases.Count);
        Assert.Equal(2, phrases.Select(identity => identity.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(phrases, identity => Assert.Contains("anna", identity.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void TheOneDeviceIdentityHoldsAKeyPerRepositoryAndEachSaysWhatItUnlocks()
    {
        // One machine, one device identity, but a key per repository - and two identical lines under
        // "This PC" tell a reader nothing about which is which.
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(id: "a", stableId: "src-a", source: @"C:\Users\anna\Documents"),
            Schedule(id: "b", stableId: "src-b", source: @"C:\Users\anna\Projects")
        ]);

        var device = Assert.Single(catalogue.Identities, identity => identity.Kind == IdentityKind.Device);
        var keys = catalogue.IdentityKeys.Where(key => key.IdentityId == device.Id).ToList();

        Assert.Equal(2, keys.Count);
        Assert.Equal(2, keys.Select(key => key.Description).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ADailyScheduleKeepsItsTimeAndItsZone()
    {
        var catalogue = LegacyScheduleProjector.Project(
            [Schedule(recurrence: new DailyAt(new TimeOnly(2, 30), Paris))]);

        var trigger = Assert.IsType<DailyTrigger>(Assert.Single(catalogue.Tasks).Trigger);
        Assert.Equal(new TimeOnly(2, 30), trigger.TimeOfDay);
        // By name. A trigger is meant to be written down and read on another machine or in another
        // year; a TimeZoneInfo is this process's idea of what that name means today.
        Assert.Equal(Paris.Id, trigger.TimeZoneId);
    }

    [Fact]
    public void AWeeklyScheduleKeepsWhichDays()
    {
        var catalogue = LegacyScheduleProjector.Project(
            [Schedule(recurrence: new DailyAt(new TimeOnly(4, 0), Paris, [DayOfWeek.Sunday]))]);

        var trigger = Assert.IsType<DailyTrigger>(Assert.Single(catalogue.Tasks).Trigger);
        Assert.Equal([DayOfWeek.Sunday], trigger.Days);
    }

    [Fact]
    public void AnIntervalScheduleKeepsItsPeriod()
    {
        var catalogue = LegacyScheduleProjector.Project(
            [Schedule(recurrence: new EveryInterval(TimeSpan.FromHours(6)))]);

        Assert.Equal(TimeSpan.FromHours(6), Assert.IsType<IntervalTrigger>(Assert.Single(catalogue.Tasks).Trigger).Period);
    }

    [Fact]
    public void TheDrillAndRetentionSchedulesSurvive()
    {
        // Dropping these would make the projection quietly lossy, and a read model that lost the
        // drill schedule would let a screen show a source as covered while nothing was proving it
        // can be restored.
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(
                drill: new EveryInterval(TimeSpan.FromDays(7)),
                retentionRecurrence: new EveryInterval(TimeSpan.FromDays(1)),
                retention: new RetentionPolicy(KeepDaily: 30, KeepMonthly: 12))
        ]);

        var route = Assert.Single(catalogue.Routes);
        Assert.Equal(TimeSpan.FromDays(7), Assert.IsType<IntervalTrigger>(route.DrillTrigger).Period);
        Assert.Equal(TimeSpan.FromDays(1), Assert.IsType<IntervalTrigger>(route.RetentionTrigger).Period);
        Assert.Equal(30, route.Retention!.KeepDaily);
        Assert.Equal(12, route.Retention.KeepMonthly);
    }

    [Fact]
    public void ARetentionPolicyThatKeepsNothingIsNoPolicy()
    {
        var catalogue = LegacyScheduleProjector.Project([Schedule(retention: new RetentionPolicy())]);

        Assert.Null(Assert.Single(catalogue.Routes).Retention);
    }

    [Fact]
    public void ADisabledScheduleProjectsAsDisabled() =>
        Assert.False(Assert.Single(LegacyScheduleProjector.Project([Schedule(enabled: false)]).Tasks).Enabled);

    [Fact]
    public void CatchUpSurvives() =>
        Assert.Equal(
            CatchUpPolicy.Skip,
            Assert.Single(LegacyScheduleProjector.Project([Schedule(catchUp: CatchUp.Skip)]).Tasks).CatchUp);

    [Fact]
    public void AnObjectStoreIsRemoteAndOverHttpsIsEncryptedInTransit()
    {
        var catalogue = LegacyScheduleProjector.Project(
            [Schedule(repository: "s3:https://minio.example/fortiq")]);

        var storage = Assert.Single(catalogue.Storages);
        Assert.Equal(StorageBackend.S3, storage.Backend);
        Assert.True(storage.Capabilities.HasFlag(StorageCapabilities.Remote));
        Assert.True(storage.Capabilities.HasFlag(StorageCapabilities.EncryptedTransport));
    }

    [Fact]
    public void NothingClaimsImmutabilityNobodyHasChecked()
    {
        // The capability that a ransomware claim would rest on. It is not readable from a URL, and a
        // projector that guessed would put an unchecked promise on somebody's screen.
        var catalogue = LegacyScheduleProjector.Project(
            [Schedule(repository: "s3:https://minio.example/fortiq")]);

        var storage = Assert.Single(catalogue.Storages);
        Assert.False(storage.Capabilities.HasFlag(StorageCapabilities.Immutable));
        Assert.False(storage.Capabilities.HasFlag(StorageCapabilities.ObjectLock));
        Assert.False(storage.Capabilities.HasFlag(StorageCapabilities.Versioned));
    }

    [Fact]
    public void AnObjectStoreNeedsACredentialAndALocalFolderDoesNot()
    {
        var remote = LegacyScheduleProjector.Project([Schedule(repository: "s3:https://minio.example/fortiq")]);
        var local = LegacyScheduleProjector.Project([Schedule(repository: @"E:\Backups")]);

        Assert.NotNull(Assert.Single(remote.Storages).CredentialRef);
        Assert.Single(remote.Credentials);
        Assert.Null(Assert.Single(local.Storages).CredentialRef);
        Assert.Empty(local.Credentials);
    }

    [Fact]
    public void NoCredentialCarriesASecret()
    {
        var catalogue = LegacyScheduleProjector.Project([Schedule(repository: "s3:https://minio.example/fortiq")]);

        var credential = Assert.Single(catalogue.Credentials);
        Assert.Equal(StorageCredentialKind.ObjectStorageKey, credential.Kind);
        // A reference and a human-readable name, and nowhere for a key to be even if somebody tried.
        Assert.Equal(3, typeof(StorageCredentialRef).GetProperties().Length);
    }

    [Fact]
    public void ProjectingTwiceProducesTheSameIdentifiers()
    {
        // These identifiers will be written to files and compared later. If they moved between runs
        // the projection could not be a read model of anything.
        var first = LegacyScheduleProjector.Project([Schedule()]);
        var second = LegacyScheduleProjector.Project([Schedule()]);

        Assert.Equal(first.Sources[0].Id, second.Sources[0].Id);
        Assert.Equal(first.Storages[0].Id, second.Storages[0].Id);
        Assert.Equal(first.Tasks[0].Id, second.Tasks[0].Id);
    }

    [Fact]
    public void TwoDifferentFoldersNeverCollapseIntoOneIdentifier()
    {
        // Slugs strip everything that is not a letter or a digit, so paths that differ only in
        // punctuation would collide were it not for the hash on the end.
        var catalogue = LegacyScheduleProjector.Project(
        [
            Schedule(id: "a", stableId: "", source: @"C:\a-b"),
            Schedule(id: "b", stableId: "", source: @"C:\a_b")
        ]);

        Assert.Equal(2, catalogue.Sources.Count);
        Assert.Equal(2, catalogue.Sources.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count());
    }

    private static BackupSchedule Schedule(
        string id = "sched-a",
        string repository = @"E:\Backups",
        string source = @"C:\Users\anna\Documents",
        string stableId = "src-documents",
        Recurrence? recurrence = null,
        Recurrence? drill = null,
        Recurrence? retentionRecurrence = null,
        RetentionPolicy? retention = null,
        bool enabled = true,
        CatchUp catchUp = CatchUp.Once) =>
        new(
            id,
            repository,
            @"F:\Recovery Kit",
            source,
            stableId,
            recurrence ?? new DailyAt(new TimeOnly(2, 0), Paris),
            DrillRecurrence: drill,
            RetentionRecurrence: retentionRecurrence,
            Retention: retention,
            Enabled: enabled,
            CatchUp: catchUp);
}
