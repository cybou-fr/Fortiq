namespace Fortiq.CommunityModel;

/// <summary>
/// One deterministic check over a proposal.
/// </summary>
/// <remarks>
/// Deterministic is the requirement, not a preference. A proposal may have been written by a
/// language model, and the only reason that is acceptable is that nothing it writes is trusted:
/// every claim it makes is re-established here by code that reads the catalogue and the proposal and
/// nothing else. Same input, same findings, no clock, no network, no model.
/// </remarks>
public interface IProposalValidator
{
    IEnumerable<ValidationFinding> Validate(TaskProposal proposal, ResourceCatalog catalog);
}

/// <summary>
/// Is it shaped like a task at all?
/// </summary>
/// <remarks>
/// First, because everything after it assumes the answer is yes. These are the failures that come
/// from a model producing plausible-looking nonsense - an empty name, a task with no routes, an
/// interval of zero - and from a form somebody has half filled in.
/// </remarks>
public sealed class SchemaValidator : IProposalValidator
{
    public IEnumerable<ValidationFinding> Validate(TaskProposal proposal, ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var task = proposal.Task;

        if (string.IsNullOrWhiteSpace(task.Id))
        {
            yield return new ValidationFinding("task-no-id", "This task has no identifier.");
        }

        if (string.IsNullOrWhiteSpace(task.Name))
        {
            yield return new ValidationFinding("task-no-name", "This task has no name, so nothing on screen could tell it from another.");
        }

        if (task.SourceIds.Count == 0)
        {
            yield return new ValidationFinding("task-no-source", "This task backs nothing up: no source was named.");
        }

        if (task.RouteIds.Count == 0)
        {
            yield return new ValidationFinding("task-no-route", "This task has nowhere to write: no copy was described.");
        }

        foreach (var finding in ValidateTrigger(task.Trigger, "task-trigger"))
        {
            yield return finding;
        }

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in proposal.Routes)
        {
            if (!routeIds.Add(route.Id))
            {
                yield return new ValidationFinding("route-duplicate", $"Two copies share the identifier '{route.Id}'.");
            }

            if (route.Retention is { } retention && !retention.KeepsSomething)
            {
                // A rule that keeps nothing is not retention, it is deletion, and it would be
                // deletion applied on a schedule to the only copies of somebody's files.
                yield return new ValidationFinding(
                    "route-retention-keeps-nothing",
                    $"The retention rule for '{route.Id}' keeps no snapshots at all, which would delete every backup it ran against.");
            }

            foreach (var finding in ValidateTrigger(route.RetentionTrigger, "route-retention-trigger"))
            {
                yield return finding;
            }

            foreach (var finding in ValidateTrigger(route.DrillTrigger, "route-drill-trigger"))
            {
                yield return finding;
            }
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in proposal.Sources.Where(source => !sourceIds.Add(source.Id)))
        {
            yield return new ValidationFinding("source-duplicate", $"Two sources share the identifier '{source.Id}'.");
        }

        foreach (var source in proposal.Sources.Where(source => string.IsNullOrWhiteSpace(source.Path)))
        {
            yield return new ValidationFinding("source-no-path", $"The source '{source.Name}' names no path.");
        }
    }

    private static IEnumerable<ValidationFinding> ValidateTrigger(Trigger? trigger, string code)
    {
        switch (trigger)
        {
            case IntervalTrigger { Period: var period } when period <= TimeSpan.Zero:
                yield return new ValidationFinding($"{code}-not-positive", "An interval of zero or less never comes due.");
                break;

            case DailyTrigger daily when string.IsNullOrWhiteSpace(daily.TimeZoneId):
                yield return new ValidationFinding($"{code}-no-time-zone", "A time of day without a time zone is not a time.");
                break;

            case DailyTrigger { Days.Count: 0 }:
                yield return new ValidationFinding($"{code}-no-days", "A weekly trigger with no days never comes due.");
                break;

            case DailyTrigger daily when !TimeZoneExists(daily.TimeZoneId):
                yield return new ValidationFinding(
                    $"{code}-unknown-time-zone",
                    $"This machine does not know a time zone called '{daily.TimeZoneId}'.");
                break;

            case FileChangeTrigger change when change.Settle < TimeSpan.Zero || change.MinimumInterval <= TimeSpan.Zero:
                yield return new ValidationFinding(
                    $"{code}-not-coalescing",
                    "A file-change trigger needs a settle window and a positive minimum interval, or a busy folder would back up without stopping.");
                break;
        }
    }

    private static bool TimeZoneExists(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

/// <summary>
/// Does everything it mentions exist?
/// </summary>
/// <remarks>
/// A proposal may introduce resources of its own, so a reference resolves against the catalogue or
/// against the proposal. What it may never do is resolve against neither: a route pointing at a
/// storage nobody has ever configured is a task that would fail on its first run, at two in the
/// morning, having reported itself as configured.
/// </remarks>
public sealed class ReferenceValidator : IProposalValidator
{
    public IEnumerable<ValidationFinding> Validate(TaskProposal proposal, ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var sourceId in proposal.Task.SourceIds)
        {
            if (catalog.Source(sourceId) is null && !proposal.Sources.Any(source => source.Id == sourceId))
            {
                yield return new ValidationFinding("source-unknown", $"There is no source called '{sourceId}'.");
            }
        }

        foreach (var routeId in proposal.Task.RouteIds)
        {
            if (!proposal.Routes.Any(route => route.Id == routeId) && catalog.Route(routeId) is null)
            {
                yield return new ValidationFinding("route-unknown", $"There is no copy called '{routeId}'.");
            }
        }

        foreach (var route in proposal.Routes)
        {
            if (catalog.Storage(route.StorageId) is null && !proposal.Storages.Any(storage => storage.Id == route.StorageId))
            {
                yield return new ValidationFinding("storage-unknown", $"There is no storage called '{route.StorageId}'.");
            }

            if (catalog.Engine(route.EngineId) is null)
            {
                yield return new ValidationFinding("engine-unknown", $"There is no repository engine called '{route.EngineId}'.");
            }

            var profile = ResolveProfile(route.EncryptionProfileId, proposal, catalog);
            if (profile is null)
            {
                yield return new ValidationFinding("encryption-profile-unknown", $"There is no encryption profile called '{route.EncryptionProfileId}'.");
                continue;
            }

            foreach (var keyId in profile.Recipients.Concat(profile.Writers))
            {
                if (catalog.IdentityKey(keyId) is null)
                {
                    yield return new ValidationFinding("identity-key-unknown", $"There is no key called '{keyId}'.");
                }
            }
        }

        foreach (var storage in proposal.Storages)
        {
            if (storage.CredentialRef is { } credential && catalog.Credential(credential) is null)
            {
                yield return new ValidationFinding("credential-unknown", $"There is no stored credential called '{credential}'.");
            }
        }
    }

    internal static EncryptionProfile? ResolveProfile(string id, TaskProposal proposal, ResourceCatalog catalog) =>
        proposal.EncryptionProfiles.FirstOrDefault(profile => profile.Id == id) ?? catalog.EncryptionProfile(id);
}

/// <summary>
/// Can this build actually do what is being asked?
/// </summary>
/// <remarks>
/// The validator that stops the model - or an over-eager form - promising something Fortiq cannot
/// deliver. A file-change trigger and an SFTP backend are both in the model because they are where
/// this is going; neither is implemented, and a task using one would be accepted, saved, shown as
/// configured, and never run. Saying so here is the difference between a feature that is missing and
/// a backup that silently is not happening.
/// </remarks>
public sealed class CapabilityValidator(CommunityCapabilities? capabilities = null) : IProposalValidator
{
    private readonly CommunityCapabilities _capabilities = capabilities ?? CommunityCapabilities.Current;

    public IEnumerable<ValidationFinding> Validate(TaskProposal proposal, ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(catalog);

        // Read from the same place the assistant is told about, so the two can never disagree. An
        // assistant that offers a trigger the validator then rejects reads as a broken product
        // rather than an absent feature.
        if (!_capabilities.Supports(proposal.Task.Trigger))
        {
            yield return new ValidationFinding(
                "trigger-not-supported",
                proposal.Task.Trigger is FileChangeTrigger
                    ? "Fortiq cannot yet run a task when files change. Choose a time instead."
                    : "Fortiq cannot yet run a task on that kind of trigger.");
        }

        foreach (var storage in AllStorages(proposal, catalog))
        {
            if (!_capabilities.Supports(storage.Backend))
            {
                yield return new ValidationFinding(
                    "storage-backend-not-supported",
                    $"Fortiq cannot yet write to '{storage.Name}' over {storage.Backend}.");
            }

            if (storage.Backend == StorageBackend.S3 && storage.CredentialRef is null)
            {
                yield return new ValidationFinding(
                    "storage-no-credential",
                    $"'{storage.Name}' is an object store and no credential was chosen, so Fortiq could not sign in to it.");
            }
        }
    }

    private static IEnumerable<Storage> AllStorages(TaskProposal proposal, ResourceCatalog catalog) =>
        proposal.Routes
            .Select(route => proposal.Storages.FirstOrDefault(storage => storage.Id == route.StorageId)
                ?? catalog.Storage(route.StorageId))
            .OfType<Storage>();
}

/// <summary>
/// Would anybody be able to get the data back, and could it be written unattended?
/// </summary>
/// <remarks>
/// The rules from Spec 29 that exist because the failure they prevent is silent. A repository with
/// no recipient is an encrypted archive nobody can open, and it looks exactly like a working one
/// until the day it is needed. A recurring task with no writer cannot unlock itself at two in the
/// morning, so it either does not run or asks a question nobody is there to answer.
///
/// An unconfirmed recovery phrase is a warning and not a block, and that line is drawn deliberately.
/// Somebody setting up a backup has not yet written down the words they are about to be shown, and
/// refusing to proceed would mean nobody could ever configure anything; the health model already
/// treats an unconfirmed phrase as putting a repository at risk, which is where that belongs.
/// </remarks>
public sealed class SecurityPolicyValidator : IProposalValidator
{
    public IEnumerable<ValidationFinding> Validate(TaskProposal proposal, ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(catalog);

        var recurring = proposal.Task.Trigger is not (ManualTrigger or OnceTrigger);

        foreach (var route in proposal.Routes)
        {
            var profile = ReferenceValidator.ResolveProfile(route.EncryptionProfileId, proposal, catalog);
            if (profile is null)
            {
                // Already reported as an unknown reference; nothing here can be said about it.
                continue;
            }

            if (profile.Recipients.Count == 0)
            {
                yield return new ValidationFinding(
                    "encryption-no-recipient",
                    $"Nobody could recover the copy '{route.Id}': its encryption profile names no recipient.");
            }

            // RULE-RESTIC-001. A recurring repository has to be able to unlock itself.
            if (recurring && profile.Writers.Count == 0)
            {
                yield return new ValidationFinding(
                    "encryption-no-writer",
                    $"'{route.Id}' would run on a schedule with nothing able to unlock it unattended, so it could never write.");
            }

            var recipients = profile.Recipients.Select(catalog.IdentityKey).OfType<IdentityKey>().ToList();
            if (recipients.Count > 0 && !recipients.Any(key => key.Confirmed))
            {
                yield return new ValidationFinding(
                    "encryption-recipient-unconfirmed",
                    $"Nobody has confirmed they hold a key that could recover '{route.Id}'.",
                    ValidationSeverity.Warning);
            }

            // RULE-KEY-001, structurally. Storage access and decryption are separate authorities, and
            // a writer that is also the only recipient collapses them into one thing on one machine.
            if (recurring
                && profile.Recipients.Count > 0
                && profile.Recipients.All(recipient => profile.Writers.Contains(recipient)))
            {
                yield return new ValidationFinding(
                    "encryption-recipient-is-only-writer",
                    $"The only way to recover '{route.Id}' is the same key this PC uses to write it, so losing this PC would lose the backups with it.");
            }
        }

        foreach (var route in proposal.Routes.Where(route => route.Retention is not null && route.RetentionTrigger is null))
        {
            yield return new ValidationFinding(
                "route-retention-never-runs",
                $"'{route.Id}' has a retention rule and nothing that runs it, so history would grow without bound.",
                ValidationSeverity.Warning);
        }
    }
}

/// <summary>
/// Runs every check, in order, and records the result on the draft.
/// </summary>
/// <remarks>
/// Order matters only for reading: schema first because everything after assumes a shape, references
/// next because the rest needs resolution, then capability and policy. Every validator runs
/// regardless - a person fixing a proposal wants the whole list, not the first item of it, five
/// times over.
/// </remarks>
public sealed class DraftValidator(IReadOnlyList<IProposalValidator>? validators = null)
{
    private readonly IReadOnlyList<IProposalValidator> _validators = validators ??
    [
        new SchemaValidator(),
        new ReferenceValidator(),
        new CapabilityValidator(),
        new SecurityPolicyValidator()
    ];

    public IReadOnlyList<ValidationFinding> Validate(TaskProposal proposal, ResourceCatalog catalog) =>
        _validators.SelectMany(validator => validator.Validate(proposal, catalog)).ToList();

    public Draft<TaskProposal> Validate(Draft<TaskProposal> draft, ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.Validated(Validate(draft.Proposed, catalog));
    }
}

/// <summary>Whether a draft may become live configuration, and why not when it may not.</summary>
public sealed record ActivationDecision(bool Allowed, IReadOnlyList<ValidationFinding> Reasons)
{
    public static ActivationDecision Yes { get; } = new(true, []);

    public static ActivationDecision No(string code, string detail) => new(false, [new ValidationFinding(code, detail)]);
}

/// <summary>
/// The last gate: may this draft be turned into configuration that runs?
/// </summary>
/// <remarks>
/// Separate from the other validators because it asks a different kind of question. They ask whether
/// a proposal is correct; this asks whether the process that produced it entitles it to take effect.
/// A proposal can be perfectly valid and still have no business becoming live - because nobody has
/// read it, or because the only thing that has read it is the model that wrote it.
///
/// RULE-TASK-002: only an explicit human act activates a task. That is enforced here, by requiring
/// the draft to have reached <see cref="DraftState.Accepted"/>, which <see cref="Draft{T}.Accept"/>
/// permits only from <see cref="DraftState.ReadyForReview"/>. There is no argument to this method
/// that a caller could pass to skip it, and that absence is the point: an assistant holding a draft
/// has no expressible way to activate it.
/// </remarks>
public static class ActivationValidator
{
    public static ActivationDecision Decide(Draft<TaskProposal> draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.State != DraftState.Accepted)
        {
            return ActivationDecision.No(
                "draft-not-accepted",
                draft.State switch
                {
                    DraftState.Draft => "This has not been checked yet.",
                    DraftState.Invalid => "This cannot be used until what is wrong with it is fixed.",
                    DraftState.ReadyForReview => "Somebody has to read this and accept it first.",
                    _ => "This was set aside."
                });
        }

        // Belt and braces. Accept cannot be reached from Invalid, so this can only fire if a draft
        // were constructed directly in the Accepted state - which is exactly the mistake worth
        // catching, because it is how a validation step gets quietly skipped.
        var blocking = draft.Blocking.ToList();
        return blocking.Count == 0
            ? ActivationDecision.Yes
            : new ActivationDecision(false, blocking);
    }
}
