using Fortiq.CommunityModel;

namespace Fortiq.CommunityModel.Tests;

/// <summary>
/// What a proposal has to survive before it can become configuration that runs.
/// </summary>
/// <remarks>
/// These checks exist because a proposal may have been written by a language model, and the only
/// thing that makes that acceptable is that nothing it says is believed: every claim is
/// re-established here by code that reads the catalogue and the proposal and nothing else.
/// </remarks>
public sealed class DraftValidationTests
{
    private readonly DraftValidator _validator = new();

    [Fact]
    public void AWellFormedProposalPassesEverything()
    {
        var findings = _validator.Validate(Proposal(), Catalog());

        Assert.DoesNotContain(findings, finding => finding.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void ATaskWithNoSourceBacksNothingUp() =>
        AssertBlocks("task-no-source", Proposal(task: Task() with { SourceIds = [] }));

    [Fact]
    public void ATaskWithNoRouteHasNowhereToWrite() =>
        AssertBlocks("task-no-route", Proposal(task: Task() with { RouteIds = [] }));

    [Fact]
    public void ATaskWithNoNameCannotBeToldFromAnother() =>
        AssertBlocks("task-no-name", Proposal(task: Task() with { Name = "  " }));

    [Fact]
    public void AnIntervalOfZeroNeverComesDue() =>
        AssertBlocks("task-trigger-not-positive", Proposal(task: Task() with { Trigger = new IntervalTrigger(TimeSpan.Zero) }));

    [Fact]
    public void ATimeOfDayWithoutAZoneIsNotATime() =>
        AssertBlocks("task-trigger-no-time-zone", Proposal(
            task: Task() with { Trigger = new DailyTrigger(new TimeOnly(2, 0), "  ") }));

    [Fact]
    public void AZoneThisMachineDoesNotKnowIsRefused() =>
        AssertBlocks("task-trigger-unknown-time-zone", Proposal(
            task: Task() with { Trigger = new DailyTrigger(new TimeOnly(2, 0), "Mars/Olympus") }));

    [Fact]
    public void AWeeklyTriggerWithNoDaysNeverComesDue() =>
        AssertBlocks("task-trigger-no-days", Proposal(
            task: Task() with { Trigger = new DailyTrigger(new TimeOnly(2, 0), "UTC", []) }));

    [Fact]
    public void ARetentionRuleThatKeepsNothingIsDeletionOnASchedule()
    {
        // The most dangerous thing a plausible-looking generated policy could contain: it would run
        // on a timer, against the only copies somebody has.
        AssertBlocks("route-retention-keeps-nothing", Proposal(
            routes: [Route() with { Retention = new RetentionRule(), RetentionTrigger = new IntervalTrigger(TimeSpan.FromDays(1)) }]));
    }

    [Fact]
    public void ASourceThatDoesNotExistIsRefused() =>
        AssertBlocks("source-unknown", Proposal(task: Task() with { SourceIds = ["source-imaginary"] }));

    [Fact]
    public void AStorageThatDoesNotExistIsRefused() =>
        AssertBlocks("storage-unknown", Proposal(routes: [Route() with { StorageId = "storage-imaginary" }]));

    [Fact]
    public void AnEngineThatDoesNotExistIsRefused() =>
        AssertBlocks("engine-unknown", Proposal(routes: [Route() with { EngineId = "engine-imaginary" }]));

    [Fact]
    public void AProposalMayIntroduceItsOwnResources()
    {
        // "Back up Projects to the NAS" can mean a task plus a source plus a storage that do not yet
        // exist, and validation has to answer the same questions either way.
        var proposal = new TaskProposal(
            Task() with { SourceIds = ["source-new"] },
            [Route() with { StorageId = "storage-new" }],
            NewSources: [new Source("source-new", "Projects", SourceKind.Folder, @"C:\Projects")],
            NewStorages: [new Storage("storage-new", "NAS", StorageBackend.FileSystem, @"\\nas\backups")]);

        Assert.DoesNotContain(
            _validator.Validate(proposal, Catalog()),
            finding => finding.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void AnEncryptionProfileWithNoRecipientIsAnArchiveNobodyCanOpen()
    {
        // It looks exactly like a working backup until the day it is needed.
        AssertBlocks("encryption-no-recipient", Proposal(
            profiles: [new EncryptionProfile("enc-a", "None", [], ["key-device"])]));
    }

    [Fact]
    public void ARecurringTaskWithNoWriterCouldNeverRunUnattended() =>
        AssertBlocks("encryption-no-writer", Proposal(
            profiles: [new EncryptionProfile("enc-a", "Phrase only", ["key-phrase"], [])]));

    [Fact]
    public void AManualTaskNeedsNoWriterBecauseSomebodyIsThere()
    {
        var findings = _validator.Validate(
            Proposal(
                task: Task() with { Trigger = new ManualTrigger() },
                profiles: [new EncryptionProfile("enc-a", "Phrase only", ["key-phrase"], [])]),
            Catalog());

        Assert.DoesNotContain(findings, finding => finding.Code == "encryption-no-writer");
    }

    [Fact]
    public void AKeyThatIsBothTheOnlyRecipientAndTheWriterLosesEverythingWithThePC() =>
        AssertBlocks("encryption-recipient-is-only-writer", Proposal(
            profiles: [new EncryptionProfile("enc-a", "Device only", ["key-device"], ["key-device"])]));

    [Fact]
    public void AnUnconfirmedPhraseWarnsAndDoesNotBlock()
    {
        // Somebody setting up a backup has not yet written down words they have not been shown.
        // Blocking here would mean nobody could ever configure anything.
        var catalog = Catalog(phraseConfirmed: false);

        var findings = _validator.Validate(Proposal(), catalog);

        var finding = Assert.Single(findings, item => item.Code == "encryption-recipient-unconfirmed");
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.DoesNotContain(findings, item => item.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void AFileChangeTriggerIsRefusedBecauseFortiqCannotRunOne()
    {
        // In the model because that is where this is going; not implemented. A task using one would
        // be saved, shown as configured, and never run.
        AssertBlocks("trigger-not-supported", Proposal(
            task: Task() with { Trigger = new FileChangeTrigger(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)) }));
    }

    [Fact]
    public void SftpIsRefusedForTheSameReason() =>
        AssertBlocks("storage-backend-not-supported", Proposal(
            routes: [Route() with { StorageId = "storage-sftp" }],
            storages: [new Storage("storage-sftp", "Server", StorageBackend.Sftp, "sftp://example/backups")]));

    [Fact]
    public void AnObjectStoreWithoutACredentialCouldNotSignIn() =>
        AssertBlocks("storage-no-credential", Proposal(
            routes: [Route() with { StorageId = "storage-s3" }],
            storages: [new Storage("storage-s3", "MinIO", StorageBackend.S3, "s3:https://minio.example/fortiq")]));

    [Fact]
    public void RetentionWithNothingToRunItWarns()
    {
        var findings = _validator.Validate(
            Proposal(routes: [Route() with { Retention = new RetentionRule(KeepDaily: 30) }]),
            Catalog());

        Assert.Equal(
            ValidationSeverity.Warning,
            Assert.Single(findings, finding => finding.Code == "route-retention-never-runs").Severity);
    }

    [Fact]
    public void EveryProblemIsReportedNotJustTheFirst()
    {
        // Somebody fixing a proposal wants the whole list, not one item of it five times over.
        var findings = _validator.Validate(
            Proposal(
                task: Task() with { Name = "", SourceIds = [], Trigger = new IntervalTrigger(TimeSpan.Zero) }),
            Catalog());

        Assert.Contains(findings, finding => finding.Code == "task-no-name");
        Assert.Contains(findings, finding => finding.Code == "task-no-source");
        Assert.Contains(findings, finding => finding.Code == "task-trigger-not-positive");
    }

    [Fact]
    public void ValidationIsDeterministic()
    {
        var first = _validator.Validate(Proposal(task: Task() with { SourceIds = [] }), Catalog());
        var second = _validator.Validate(Proposal(task: Task() with { SourceIds = [] }), Catalog());

        Assert.Equal(first.Select(finding => finding.Code), second.Select(finding => finding.Code));
    }

    private void AssertBlocks(string code, TaskProposal proposal)
    {
        var findings = _validator.Validate(proposal, Catalog());

        Assert.Contains(findings, finding => finding.Code == code && finding.Severity == ValidationSeverity.Blocking);
    }

    internal static BackupTask Task() => new(
        "task-documents",
        "Documents",
        ["source-documents"],
        new DailyTrigger(new TimeOnly(2, 0), "UTC"),
        ["route-a"]);

    internal static BackupRoute Route() => new("route-a", "storage-local", "engine-restic", "enc-a");

    internal static TaskProposal Proposal(
        BackupTask? task = null,
        IReadOnlyList<BackupRoute>? routes = null,
        IReadOnlyList<Storage>? storages = null,
        IReadOnlyList<EncryptionProfile>? profiles = null) =>
        new(task ?? Task(), routes ?? [Route()], NewStorages: storages, NewEncryptionProfiles: profiles);

    internal static ResourceCatalog Catalog(bool phraseConfirmed = true) => new(
        [new Source("source-documents", "Documents", SourceKind.Folder, @"C:\Users\anna\Documents")],
        [new Storage("storage-local", "External disk", StorageBackend.FileSystem, @"E:\Backups")],
        [],
        [new RepositoryEngineRef("engine-restic", "restic", "0.19.1")],
        [
            new Identity("identity-phrase", "Recovery phrase", IdentityKind.PaperRecovery),
            new Identity("identity-pc", "This PC", IdentityKind.Device)
        ],
        [
            new IdentityKey("key-phrase", "identity-phrase", IdentityKeyKind.RecoveryPhrase, "24 words", phraseConfirmed),
            new IdentityKey("key-device", "identity-pc", IdentityKeyKind.DeviceBound, "Sealed to this machine", true)
        ],
        [new EncryptionProfile("enc-a", "Personal recovery", ["key-phrase"], ["key-device"])],
        [],
        []);
}
