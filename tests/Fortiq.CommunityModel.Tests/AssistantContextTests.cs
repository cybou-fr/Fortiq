using Fortiq.CommunityModel;

namespace Fortiq.CommunityModel.Tests;

/// <summary>
/// What the assistant is told before it is asked anything.
/// </summary>
/// <remarks>
/// Two properties matter more than the rest. It must not carry a secret, because everything it says
/// ends up in a prompt. And it must not claim a capability this build lacks, because an assistant
/// that offers a file-change trigger the validator then refuses reads as a broken product rather
/// than an absent feature.
/// </remarks>
public sealed class AssistantContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly AssistantContextBuilder _builder = new();

    [Fact]
    public void TheRulesAreAlwaysThere()
    {
        var rendered = _builder.Build(Catalog()).Render();

        Assert.Contains("RULE-TASK-001", rendered, StringComparison.Ordinal);
        Assert.Contains("RULE-RECOVERY-001", rendered, StringComparison.Ordinal);
        Assert.Contains("RULE-SECRET-001", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAssistantIsToldItDecidesNothingAboutRecovery()
    {
        // The rule that matters most on a product whose whole claim is evidence-backed recovery.
        var rule = ProductRules.EvidenceDecidesRecovery;

        Assert.Contains("evidence", rule.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not", rule.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatThisBuildCannotDoIsStatedAsSuch()
    {
        var rendered = _builder.Build(Catalog()).Render();

        Assert.Contains("Anything not listed here does not work yet", rendered, StringComparison.Ordinal);
        Assert.Contains("FileSystem, S3", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Sftp", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("FileChangeTrigger", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCapabilitiesToldAreTheCapabilitiesEnforced()
    {
        // One source of truth. Two lists would drift within a release, and the symptom would be an
        // assistant confidently offering something that is rejected the moment somebody accepts it.
        var capabilities = CommunityCapabilities.Current;

        Assert.False(capabilities.Supports(StorageBackend.Sftp));
        Assert.False(capabilities.Supports(new FileChangeTrigger(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30))));
        Assert.True(capabilities.Supports(new DailyTrigger(new TimeOnly(2, 0), "UTC")));
    }

    [Fact]
    public void AMachineProtectingNothingSaysSoPlainly() =>
        Assert.Contains("Nothing is protected yet", _builder.Build(ResourceCatalog.Empty).Render(), StringComparison.Ordinal);

    [Fact]
    public void ATaskIsDescribedByWhatItProtectsAndWhen()
    {
        var rendered = _builder.Build(Catalog()).Render();

        Assert.Contains("task Documents", rendered, StringComparison.Ordinal);
        Assert.Contains("daily at 02:00 UTC", rendered, StringComparison.Ordinal);
        Assert.Contains(@"from C:\Users\anna\Documents", rendered, StringComparison.Ordinal);
        Assert.Contains("to External disk", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfirmedRecoveryKeyIsSaidOutLoud()
    {
        // The single most important thing about a repository, and the thing a person is least likely
        // to have noticed. If the assistant is not told, it will describe the backup as fine.
        var rendered = _builder.Build(Catalog(phraseConfirmed: false)).Render();

        Assert.Contains("recovery key never confirmed", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDrillIsSaidOutLoudToo() =>
        Assert.Contains("no recovery drill", _builder.Build(Catalog()).Render(), StringComparison.Ordinal);

    [Fact]
    public void ACredentialAppearsAsStateAndNeverAsAValue()
    {
        // Spec 29: secrets are represented as state. The model has nowhere to hold one anyway, which
        // is what makes this structural rather than a promise - but the rendering must not leak the
        // reference either.
        var rendered = _builder.Build(CatalogWithObjectStorage()).Render();

        Assert.Contains("credential configured", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("cred-minio", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObjectStoreWithNoCredentialIsAlsoSaid()
    {
        var catalog = CatalogWithObjectStorage(credential: null);

        Assert.Contains("no credential configured", _builder.Build(catalog).Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInTheContextIsASecret()
    {
        // A crude but load-bearing check: the rendered context goes verbatim into a prompt, and the
        // only defence against a secret arriving there is that the model has no field to carry one.
        //
        // Everything after the rules block, because the rules themselves say the words - RULE-SECRET
        // is the sentence telling the assistant it is never given recovery phrases or passwords, and
        // an earlier version of this test flagged Fortiq's own instruction not to leak secrets.
        var rendered = _builder.Build(
            CatalogWithObjectStorage(),
            facts: [new OperationalFact("backup-failed", "Documents", "The engine reported: repository is locked.")],
            drafts: []).Render();

        var machineData = rendered[rendered.IndexOf("THIS BUILD CAN", StringComparison.Ordinal)..];

        foreach (var forbidden in new[] { "password", "secret", "AKIA", "BEGIN PRIVATE KEY", "mnemonic" })
        {
            Assert.DoesNotContain(forbidden, machineData, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RecordedFactsAreCarriedAndBounded()
    {
        // A machine with years of history has thousands, and the conversation needs the window more.
        var many = Enumerable.Range(0, 100)
            .Select(index => new OperationalFact("backup-ok", $"Source {index}", "Backed up."))
            .ToList();

        var context = _builder.Build(Catalog(), many, factLimit: 5);

        Assert.Equal(5, context.OperationalFacts.Count);
        Assert.Contains("Source 0: Backed up.", context.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void DraftsWaitingForAPersonAreListedWithWhoWroteThem()
    {
        // What an assistant is most likely to misremember, and most damaging to misremember: "I set
        // that up" about something still awaiting review is how somebody stops checking.
        var draft = new DraftValidator()
            .Validate(
                new Draft<TaskProposal>("draft-1", DraftValidationTests.Proposal(), DraftOrigin.Assistant, Now),
                Catalog());

        var rendered = _builder.Build(Catalog(), drafts: [draft]).Render();

        Assert.Contains("you proposed 'Documents'", rendered, StringComparison.Ordinal);
        Assert.Contains("waiting for a person to accept it", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidDraftSaysWhyItCannotBeUsed()
    {
        var draft = new DraftValidator()
            .Validate(
                new Draft<TaskProposal>(
                    "draft-1",
                    DraftValidationTests.Proposal(task: DraftValidationTests.Task() with { RouteIds = [] }),
                    DraftOrigin.Assistant,
                    Now),
                Catalog());

        var rendered = _builder.Build(Catalog(), drafts: [draft]).Render();

        Assert.Contains("cannot be used", rendered, StringComparison.Ordinal);
        Assert.Contains("nowhere to write", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcceptedDraftIsNotListedAsWaiting()
    {
        var accepted = new DraftValidator()
            .Validate(new Draft<TaskProposal>("draft-1", DraftValidationTests.Proposal(), DraftOrigin.Person, Now), Catalog())
            .Accept(Now);

        Assert.Empty(_builder.Build(Catalog(), drafts: [accepted]).DraftSummaries);
    }

    [Fact]
    public void TheSameMachineAlwaysProducesTheSameContext()
    {
        // It is an input to a model. If it moved between calls, the same question would get
        // different answers for reasons nobody could reconstruct.
        Assert.Equal(_builder.Build(Catalog()).Render(), _builder.Build(Catalog()).Render());
    }

    [Fact]
    public void ContextForAnOrdinaryMachineIsSmall()
    {
        // The budget is a small model's window, most of which belongs to the conversation. Roughly
        // four characters to a token, so this is well under a thousand tokens.
        var rendered = _builder.Build(Catalog()).Render();

        Assert.True(rendered.Length < 3000, $"The context was {rendered.Length} characters.");
    }

    private static ResourceCatalog Catalog(bool phraseConfirmed = true) =>
        DraftValidationTests.Catalog(phraseConfirmed) with
        {
            Routes = [DraftValidationTests.Route()],
            Tasks = [DraftValidationTests.Task()]
        };

    private static ResourceCatalog CatalogWithObjectStorage(string? credential = "cred-minio") =>
        Catalog() with
        {
            Storages = [new Storage("storage-local", "Home MinIO", StorageBackend.S3, "s3:https://minio.example/fortiq", credential)],
            Credentials = credential is null ? [] : [new StorageCredentialRef(credential, "MinIO login", StorageCredentialKind.ObjectStorageKey)]
        };
}
