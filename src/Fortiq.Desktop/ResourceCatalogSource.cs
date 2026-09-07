using Fortiq.CommunityModel;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>What this machine has, and what Fortiq recorded about it.</summary>
/// <param name="Catalog">The resources, projected from the schedules.</param>
/// <param name="Facts">What the health report says, in the words it chose.</param>
/// <param name="Health">The health of each repository, for screens that show verdicts.</param>
public sealed record MachineState(
    ResourceCatalog Catalog,
    IReadOnlyList<OperationalFact> Facts,
    IReadOnlyList<RepositoryHealth> Health)
{
    public static MachineState Empty { get; } = new(ResourceCatalog.Empty, [], []);

    /// <summary>The health of the repository a route writes to, when it can be found.</summary>
    /// <remarks>
    /// Matched on the schedule identifier the projector derived the route from. Not a join the
    /// resource model should know about - it exists only while configuration and health are two
    /// files describing the same thing by different names, and it goes when P3 does.
    /// </remarks>
    public RepositoryHealth? HealthOf(BackupTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var sources = Catalog.SourcesOf(task);
        var path = sources.Count > 0 ? sources[0].Path : null;
        return path is null
            ? null
            : Health.FirstOrDefault(repository =>
                string.Equals(repository.Facts.SourcePath, path, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Reads this machine once, for everything that needs to know what is on it.
/// </summary>
/// <remarks>
/// The screens and the assistant read the same object. That is the point of having done the
/// projection at all: an interface listing tasks and an assistant describing them must not be two
/// separate readings of the same files, because the day they disagree is the day somebody believes
/// the wrong one.
///
/// Nothing here fails loudly. A machine whose schedules cannot be read shows fewer tasks, not an
/// error page - the same judgement made everywhere else in this application, that a component which
/// cannot answer must not become a component that blocks.
/// </remarks>
public sealed class ResourceCatalogSource(IScheduleStore schedules, IHealthSource health)
{
    public async Task<MachineState> ReadAsync(CancellationToken cancellationToken)
    {
        var facts = new List<OperationalFact>();

        IReadOnlyList<RepositoryHealth> repositories = [];
        try
        {
            var result = await health.ReadAsync(cancellationToken);
            repositories = result.Report?.Repositories ?? [];

            if (result.State is HealthStoreState.Stale or HealthStoreState.Corrupt)
            {
                // A stale report describes a machine as it was, in the present tense.
                facts.Add(new OperationalFact(
                    "health-not-current",
                    "Fortiq",
                    result.Detail ?? "The health report is not current, so what follows may be out of date."));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            facts.Add(new OperationalFact("health-unreadable", "Fortiq", "The health report could not be read, so nothing below is current."));
        }

        var catalog = ResourceCatalog.Empty;
        try
        {
            var loaded = await schedules.ReadSchedulesAsync(cancellationToken);
            catalog = LegacyScheduleProjector.Project(loaded, ScheduleFactsFrom(repositories));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            facts.Add(new OperationalFact("schedules-unreadable", "Fortiq", "The backup schedules could not be read."));
        }

        facts.AddRange(repositories.SelectMany(Describe));
        return new MachineState(catalog, facts, repositories);
    }

    /// <summary>
    /// What the health report knows that a schedule file cannot.
    /// </summary>
    /// <remarks>
    /// A schedule says a repository exists; only the report says whether anybody has confirmed they
    /// hold the phrase that opens it. Without this join the catalogue would present every recovery
    /// key as a recovery route, which is the claim Fortiq is built not to make.
    /// </remarks>
    private static List<ScheduleFacts> ScheduleFactsFrom(IReadOnlyList<RepositoryHealth> repositories) =>
        repositories
            .Where(repository => repository.ScheduleId is { Length: > 0 })
            .Select(repository => new ScheduleFacts(
                repository.ScheduleId!,
                RecoveryPhraseConfirmed: repository.Facts.RecoveryPhrase == RecoveryPhraseState.Confirmed))
            .ToList();

    /// <summary>
    /// The recorded truth about one repository, in the words the health model already chose.
    /// </summary>
    /// <remarks>
    /// Findings are passed through rather than paraphrased. They are the sentences Fortiq shows on
    /// its own screens, and restating them elsewhere would be a second, slightly different account
    /// of what is wrong.
    /// </remarks>
    private static IEnumerable<OperationalFact> Describe(RepositoryHealth repository)
    {
        var subject = repository.Facts.SourcePath is { Length: > 0 } path ? path : repository.RepositoryId;

        yield return new OperationalFact(
            "verdict",
            subject,
            repository.Verdict switch
            {
                HealthVerdict.Recoverable => "Recoverable: checked and restored recently.",
                HealthVerdict.Unproven => "Backed up, but recovery has not been proven.",
                _ => "At risk: this may not be recoverable today."
            });

        if (repository.Facts.LastBackupAt is { } lastBackup)
        {
            yield return new OperationalFact("last-backup", subject, $"Last backed up {lastBackup.ToLocalTime():yyyy-MM-dd HH:mm}.");
        }

        foreach (var finding in repository.Findings)
        {
            yield return new OperationalFact(finding.Code, subject, finding.Detail);
        }
    }
}
