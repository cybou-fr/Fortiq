using Fortiq.CommunityModel;

namespace Fortiq.CommunityModel.Tests;

/// <summary>
/// The path a proposal has to walk before anything of somebody's changes.
/// </summary>
/// <remarks>
/// This is the part that has to hold even when everything else is wrong. The alternative to a draft
/// is an assistant editing live backup configuration, and the distance between "Fortiq proposes to
/// stop keeping monthly snapshots" and "Fortiq has stopped keeping monthly snapshots" is somebody's
/// data.
/// </remarks>
public sealed class DraftLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly DraftValidator _validator = new();

    [Fact]
    public void ANewDraftIsNotYetAnything()
    {
        var draft = New();

        Assert.Equal(DraftState.Draft, draft.State);
        Assert.Empty(draft.Results);
        Assert.False(ActivationValidator.Decide(draft).Allowed);
    }

    [Fact]
    public void AValidProposalBecomesReadyForSomebodyToRead()
    {
        var draft = _validator.Validate(New(), DraftValidationTests.Catalog());

        Assert.Equal(DraftState.ReadyForReview, draft.State);
        // Ready to read is not ready to run.
        Assert.False(ActivationValidator.Decide(draft).Allowed);
    }

    [Fact]
    public void AProposalWithSomethingBlockingBecomesInvalid()
    {
        var draft = _validator.Validate(
            New(DraftValidationTests.Proposal(task: DraftValidationTests.Task() with { RouteIds = [] })),
            DraftValidationTests.Catalog());

        Assert.Equal(DraftState.Invalid, draft.State);
        Assert.NotEmpty(draft.Blocking);
    }

    [Fact]
    public void AWarningDoesNotStopADraftBeingReadyToRead()
    {
        var draft = _validator.Validate(New(), DraftValidationTests.Catalog(phraseConfirmed: false));

        Assert.Equal(DraftState.ReadyForReview, draft.State);
        Assert.Contains(draft.Results, finding => finding.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void AnUncheckedDraftCannotBeAccepted() =>
        Assert.Throws<InvalidOperationException>(() => New().Accept(Now));

    [Fact]
    public void AnInvalidDraftCannotBeAccepted()
    {
        // Accepting an invalid draft would make validation advisory, which is a way of arriving at
        // a backup policy nobody checked.
        var draft = _validator.Validate(
            New(DraftValidationTests.Proposal(task: DraftValidationTests.Task() with { SourceIds = [] })),
            DraftValidationTests.Catalog());

        Assert.Throws<InvalidOperationException>(() => draft.Accept(Now));
    }

    [Fact]
    public void OnlyAnAcceptedDraftMayActivate()
    {
        var draft = _validator.Validate(New(), DraftValidationTests.Catalog()).Accept(Now);

        Assert.Equal(DraftState.Accepted, draft.State);
        Assert.True(ActivationValidator.Decide(draft).Allowed);
        Assert.Equal(Now, draft.AcceptedAt);
    }

    [Fact]
    public void AnAssistantsDraftWalksExactlyTheSamePath()
    {
        // RULE-TASK-001 and RULE-TASK-002. There is no argument to Decide that a caller could pass
        // to skip review, and no state an assistant can put a draft into that reaches Accepted; the
        // absence of that route is the enforcement.
        var proposed = New(origin: DraftOrigin.Assistant);

        Assert.False(ActivationValidator.Decide(proposed).Allowed);

        var checkedDraft = _validator.Validate(proposed, DraftValidationTests.Catalog());
        Assert.False(ActivationValidator.Decide(checkedDraft).Allowed);

        var accepted = checkedDraft.Accept(Now);
        Assert.True(ActivationValidator.Decide(accepted).Allowed);
        // And what a person accepted still says who wrote it.
        Assert.Equal(DraftOrigin.Assistant, accepted.Origin);
    }

    [Fact]
    public void RevalidatingClearsAnEarlierAcceptance()
    {
        // Something accepted last week and no longer valid - a folder deleted, a bucket that no
        // longer answers - must not stay accepted because it once was.
        var accepted = _validator.Validate(New(), DraftValidationTests.Catalog()).Accept(Now);

        var again = _validator.Validate(accepted, DraftValidationTests.Catalog());

        Assert.Equal(DraftState.ReadyForReview, again.State);
        Assert.Null(again.AcceptedAt);
        Assert.False(ActivationValidator.Decide(again).Allowed);
    }

    [Fact]
    public void ADraftAcceptedAndThenBrokenCannotActivate()
    {
        var accepted = _validator.Validate(New(), DraftValidationTests.Catalog()).Accept(Now);

        // The storage it wrote to is gone from the catalogue.
        var emptied = _validator.Validate(accepted, ResourceCatalog.Empty);

        Assert.Equal(DraftState.Invalid, emptied.State);
        Assert.False(ActivationValidator.Decide(emptied).Allowed);
    }

    [Fact]
    public void ADraftConstructedStraightIntoAcceptedStillCannotActivateIfItIsBroken()
    {
        // Belt and braces, and the mistake worth catching: this is how a validation step gets
        // quietly skipped by a caller assembling a record by hand.
        var forged = New() with
        {
            State = DraftState.Accepted,
            Findings = [new ValidationFinding("route-unknown", "There is no copy called 'route-a'.")]
        };

        var decision = ActivationValidator.Decide(forged);

        Assert.False(decision.Allowed);
        Assert.Equal("route-unknown", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void AnArchivedDraftIsKeptAndCannotActivate()
    {
        var archived = _validator.Validate(New(), DraftValidationTests.Catalog()).Accept(Now).Archive();

        Assert.Equal(DraftState.Archived, archived.State);
        Assert.False(ActivationValidator.Decide(archived).Allowed);
        // Kept, not deleted: what was proposed and refused is worth being able to read.
        Assert.NotNull(archived.Proposed);
    }

    [Fact]
    public void EachRefusalSaysWhichOneItIs()
    {
        Assert.Equal("draft-not-accepted", Assert.Single(ActivationValidator.Decide(New()).Reasons).Code);

        var ready = _validator.Validate(New(), DraftValidationTests.Catalog());
        Assert.Contains("read this and accept", Assert.Single(ActivationValidator.Decide(ready).Reasons).Detail, StringComparison.Ordinal);
    }

    private static Draft<TaskProposal> New(TaskProposal? proposal = null, DraftOrigin origin = DraftOrigin.Person) =>
        new("draft-1", proposal ?? DraftValidationTests.Proposal(), origin, Now);
}
