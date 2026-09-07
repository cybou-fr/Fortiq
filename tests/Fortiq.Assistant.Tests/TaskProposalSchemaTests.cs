using Fortiq.Assistant;
using Fortiq.CommunityModel;

namespace Fortiq.Assistant.Tests;

/// <summary>
/// Turning what a model said into a proposal against this machine.
/// </summary>
/// <remarks>
/// Most of what matters here is what the composer refuses to do. It resolves names against the
/// catalogue and never creates anything to satisfy a reference, so a proposal that mentions a
/// storage nobody has fails validation with a sentence somebody can act on, rather than becoming a
/// task that looks configured and fails at two in the morning.
/// </remarks>
public sealed class TaskProposalSchemaTests
{
    private readonly DraftValidator _validator = new();

    [Fact]
    public void AFolderAScheduleAndAKnownStorageBecomeAValidProposal()
    {
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Projects every 6 hours","sourcePath":"C:\\Projects","storage":"External disk","schedule":{"kind":"everyHours","hours":6}}""",
            Catalog());

        Assert.NotNull(proposal);
        Assert.Equal("Projects every 6 hours", proposal.Task.Name);
        Assert.Equal(TimeSpan.FromHours(6), Assert.IsType<IntervalTrigger>(proposal.Task.Trigger).Period);
        Assert.DoesNotContain(
            _validator.Validate(proposal, Catalog()),
            finding => finding.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void AStorageIsMatchedByTheNameAPersonWouldUse()
    {
        // Somebody says "the NAS" and the model repeats it. Requiring an identifier here would mean
        // requiring the model to invent one, which is the thing this design exists to prevent.
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"external disk","schedule":{"kind":"manual"}}""",
            Catalog());

        Assert.Equal("storage-local", Assert.Single(proposal!.Routes).StorageId);
    }

    [Fact]
    public void AStorageNobodyHasIsRefusedRatherThanCreated()
    {
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"Imaginary NAS","schedule":{"kind":"manual"}}""",
            Catalog());

        Assert.NotNull(proposal);
        Assert.Empty(proposal.Storages);
        Assert.Contains(
            _validator.Validate(proposal, Catalog()),
            finding => finding.Code == "storage-unknown" && finding.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void WhoCanDecryptIsNeverInferredFromASentence()
    {
        // Taken from a route that already writes to that storage, or left empty. Composing an
        // encryption profile would mean deciding who can recover somebody's data from a sentence.
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"manual"}}""",
            Catalog() with { Routes = [] });

        Assert.Equal(string.Empty, Assert.Single(proposal!.Routes).EncryptionProfileId);
        Assert.Contains(
            _validator.Validate(proposal, Catalog() with { Routes = [] }),
            finding => finding.Code == "encryption-profile-unknown");
    }

    [Fact]
    public void AnExistingProfileForThatStorageIsReused()
    {
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"manual"}}""",
            Catalog());

        Assert.Equal("enc-a", Assert.Single(proposal!.Routes).EncryptionProfileId);
    }

    [Fact]
    public void AFolderAlreadyProtectedDoesNotBecomeASecondSource()
    {
        // "Back up Documents again, to the NAS" is one folder and two copies, not two folders.
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Documents to disk","sourcePath":"C:\\Users\\anna\\Documents","storage":"External disk","schedule":{"kind":"manual"}}""",
            Catalog());

        Assert.Empty(proposal!.Sources);
        Assert.Equal("source-documents", Assert.Single(proposal.Task.SourceIds));
    }

    [Fact]
    public void ANewFolderIsCarriedByTheProposal()
    {
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Projects","sourcePath":"D:\\Projects","storage":"External disk","schedule":{"kind":"manual"}}""",
            Catalog());

        var source = Assert.Single(proposal!.Sources);
        Assert.Equal(@"D:\Projects", source.Path);
        Assert.Equal("Projects", source.Name);
    }

    [Fact]
    public void ADailyTimeKeepsTheMachinesOwnZone()
    {
        // A model guessing a zone would be guessing which hour of somebody's night their disk spins.
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"daily","timeOfDay":"03:30"}}""",
            Catalog());

        var trigger = Assert.IsType<DailyTrigger>(proposal!.Task.Trigger);
        Assert.Equal(new TimeOnly(3, 30), trigger.TimeOfDay);
        Assert.Equal(TimeZoneInfo.Local.Id, trigger.TimeZoneId);
    }

    [Fact]
    public void RetentionArrivesWithSomethingToRunIt()
    {
        // A retention rule and no trigger is history that grows without bound, which the policy
        // validator warns about. Composing the pair avoids proposing that in the first place.
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"manual"},"keepDaily":30,"keepMonthly":12}""",
            Catalog());

        var route = Assert.Single(proposal!.Routes);
        Assert.Equal(30, route.Retention!.KeepDaily);
        Assert.NotNull(route.RetentionTrigger);
    }

    [Fact]
    public void NoRetentionMeansNoRetentionTrigger() =>
        Assert.Null(Assert.Single(TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"manual"}}""",
            Catalog())!.Routes).RetentionTrigger);

    [Fact]
    public void AnIntervalOfZeroIsComposedAndThenRefused()
    {
        // Not silently corrected. A model that said "every 0 hours" said something wrong, and the
        // person reviewing the draft should see that rather than a schedule nobody asked for.
        var proposal = TaskProposalSchema.Compose(
            """{"name":"Docs","sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"everyHours","hours":0}}""",
            Catalog());

        Assert.Contains(
            _validator.Validate(proposal!, Catalog()),
            finding => finding.Code == "task-trigger-not-positive");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"sourcePath":"C:\\Docs","storage":"External disk","schedule":{"kind":"manual"}}""")]
    [InlineData("""{"name":"Docs","storage":"External disk","schedule":{"kind":"manual"}}""")]
    [InlineData("""{"name":"Docs","sourcePath":"C:\\Docs","schedule":{"kind":"manual"}}""")]
    public void SomethingThatIsNotAProposalIsNotOne(string json) =>
        Assert.Null(TaskProposalSchema.Compose(json, Catalog()));

    private static ResourceCatalog Catalog() => new(
        [new Source("source-documents", "Documents", SourceKind.Folder, @"C:\Users\anna\Documents")],
        [new Storage("storage-local", "External disk", StorageBackend.FileSystem, @"E:\Backups")],
        [],
        [new RepositoryEngineRef("engine-restic", "restic", "0.19.1")],
        [
            new Identity("identity-phrase", "Recovery phrase", IdentityKind.PaperRecovery),
            new Identity("identity-pc", "This PC", IdentityKind.Device)
        ],
        [
            new IdentityKey("key-phrase", "identity-phrase", IdentityKeyKind.RecoveryPhrase, "24 words", true),
            new IdentityKey("key-device", "identity-pc", IdentityKeyKind.DeviceBound, "Sealed to this machine", true)
        ],
        [new EncryptionProfile("enc-a", "Personal recovery", ["key-phrase"], ["key-device"])],
        [new BackupRoute("route-existing", "storage-local", "engine-restic", "enc-a")],
        []);
}
