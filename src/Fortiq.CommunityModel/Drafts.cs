namespace Fortiq.CommunityModel;

/// <summary>Who proposed something. Recorded, because it changes how it must be treated.</summary>
public enum DraftOrigin
{
    /// <summary>A person filled in a form.</summary>
    Person,

    /// <summary>
    /// A local model proposed it from something somebody said.
    /// </summary>
    /// <remarks>
    /// RULE-TASK-001: anything a model produces is a draft, never a configuration. This is not the
    /// mechanism that enforces it - <see cref="ActivationValidator"/> is - but it is what makes the
    /// provenance visible on the review screen. Somebody approving a backup policy is entitled to
    /// know that a language model wrote it.
    /// </remarks>
    Assistant
}

/// <summary>How much a finding matters.</summary>
public enum ValidationSeverity
{
    /// <summary>Worth saying, and not a reason to stop.</summary>
    Warning,

    /// <summary>This cannot be activated. No amount of confirming makes it safe.</summary>
    Blocking
}

/// <summary>
/// One thing wrong with a proposal, in a code and in words.
/// </summary>
/// <param name="Code">Stable, greppable, and what tests and screens key on.</param>
/// <param name="Detail">What a person needs to read to fix it.</param>
/// <param name="Severity">Whether it stops activation.</param>
/// <remarks>
/// The same shape the health model uses for its findings, on purpose: Fortiq already has one way of
/// saying "here is what is wrong and here is how much it matters", and a second one would mean two
/// vocabularies on screens people read while something has gone wrong.
/// </remarks>
public sealed record ValidationFinding(string Code, string Detail, ValidationSeverity Severity = ValidationSeverity.Blocking);

/// <summary>Where a draft is in its life.</summary>
public enum DraftState
{
    /// <summary>Written down and not yet validated. Nothing may be concluded from it.</summary>
    Draft,

    /// <summary>Validated, and something blocking was found.</summary>
    Invalid,

    /// <summary>Validated, nothing blocking. A person may now look at it.</summary>
    ReadyForReview,

    /// <summary>A person looked at it and accepted it. Still not running.</summary>
    Accepted,

    /// <summary>Set aside. Kept, because what was proposed and refused is worth being able to read.</summary>
    Archived
}

/// <summary>
/// Something proposed but not in force.
/// </summary>
/// <typeparam name="T">What is being proposed.</typeparam>
/// <param name="Id">Stable identifier for this proposal.</param>
/// <param name="Proposed">The thing itself.</param>
/// <param name="Origin">Who proposed it.</param>
/// <param name="CreatedAt">When.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="Findings">What validation said, the last time it ran.</param>
/// <param name="AcceptedAt">When a person accepted it, if one has.</param>
/// <remarks>
/// A draft is the one place a proposal can exist without being true. That matters more here than in
/// most systems: the alternative to a draft is an assistant that edits live backup configuration,
/// and the difference between "Fortiq proposes to stop keeping monthly snapshots" and "Fortiq has
/// stopped keeping monthly snapshots" is somebody's data.
///
/// The state is never simply assigned. It is what validation and an explicit human act produce -
/// see <see cref="Validate"/> and <see cref="Accept"/> - so there is no way to write a draft into
/// <see cref="DraftState.Accepted"/> without having gone through both.
/// </remarks>
public sealed record Draft<T>(
    string Id,
    T Proposed,
    DraftOrigin Origin,
    DateTimeOffset CreatedAt,
    DraftState State = DraftState.Draft,
    IReadOnlyList<ValidationFinding>? Findings = null,
    DateTimeOffset? AcceptedAt = null)
{
    public IReadOnlyList<ValidationFinding> Results => Findings ?? [];

    /// <summary>Everything that stops this being activated.</summary>
    public IEnumerable<ValidationFinding> Blocking =>
        Results.Where(finding => finding.Severity == ValidationSeverity.Blocking);

    /// <summary>Records what validation found, and derives the state from it.</summary>
    public Draft<T> Validated(IReadOnlyList<ValidationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return this with
        {
            Findings = findings,
            State = findings.Any(finding => finding.Severity == ValidationSeverity.Blocking)
                ? DraftState.Invalid
                : DraftState.ReadyForReview,
            // Re-validating clears an earlier acceptance. Something accepted last week and no longer
            // valid - a folder that has been deleted, a bucket that no longer answers - must not
            // stay accepted because it once was.
            AcceptedAt = null
        };
    }

    /// <summary>
    /// A person has read this and said yes.
    /// </summary>
    /// <remarks>
    /// Only from <see cref="DraftState.ReadyForReview"/>. Accepting an unvalidated draft would make
    /// validation optional, and accepting an invalid one would make it advisory; both are ways of
    /// arriving at a backup policy nobody checked.
    /// </remarks>
    public Draft<T> Accept(DateTimeOffset at) =>
        State == DraftState.ReadyForReview
            ? this with { State = DraftState.Accepted, AcceptedAt = at }
            : throw new InvalidOperationException(
                $"A draft in state {State} cannot be accepted; only a validated draft with nothing blocking can.");

    /// <summary>Set aside without deleting. What was refused is worth being able to read later.</summary>
    public Draft<T> Archive() => this with { State = DraftState.Archived };
}

/// <summary>
/// A proposed backup task, together with anything new it needs to exist.
/// </summary>
/// <param name="Task">What to back up and when.</param>
/// <param name="Routes">The copies it would produce.</param>
/// <param name="NewSources">Sources this proposal introduces.</param>
/// <param name="NewStorages">Storages this proposal introduces.</param>
/// <param name="NewEncryptionProfiles">Encryption profiles this proposal introduces.</param>
/// <remarks>
/// Self-contained on purpose. "Back up Projects every six hours to the NAS" may mean one new task
/// referring only to things that exist, or a task plus a source plus a storage plus a policy for who
/// can decrypt it - and validation has to be able to answer the same questions either way. A
/// proposal that referred to resources it did not carry and did not find would be one whose
/// correctness depended on the order things were saved in.
/// </remarks>
public sealed record TaskProposal(
    BackupTask Task,
    IReadOnlyList<BackupRoute> Routes,
    IReadOnlyList<Source>? NewSources = null,
    IReadOnlyList<Storage>? NewStorages = null,
    IReadOnlyList<EncryptionProfile>? NewEncryptionProfiles = null)
{
    public IReadOnlyList<Source> Sources => NewSources ?? [];

    public IReadOnlyList<Storage> Storages => NewStorages ?? [];

    public IReadOnlyList<EncryptionProfile> EncryptionProfiles => NewEncryptionProfiles ?? [];
}
