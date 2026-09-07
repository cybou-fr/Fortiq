using Fortiq.CommunityModel;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>
/// Assembles what the assistant is told about this machine, from what this machine actually has.
/// </summary>
/// <remarks>
/// The seam where the resource model stops being a projection nobody reads and starts being what the
/// assistant speaks from. Schedules are read, projected into a catalogue, joined to the health report
/// for the facts a schedule file cannot know, and rendered.
///
/// It reads rather than computes, and the join is the point. Whether a recovery phrase was confirmed
/// and whether a repository is recoverable are decided by the health model; a context builder that
/// worked either of them out for itself would become a second opinion on whether somebody's data can
/// be got back, which is the one thing the assistant must never be.
///
/// Failure is not fatal here. A machine whose schedules cannot be read is one whose assistant knows
/// less, not one whose assistant refuses to answer - the same judgement as the startup gate: this
/// component must never be the reason somebody cannot use Fortiq.
/// </remarks>
public sealed class AssistantContextAdapter(IScheduleStore schedules, IHealthSource health)
{
    private readonly AssistantContextBuilder _builder = new();

    public async Task<string> PrepareAsync(CancellationToken cancellationToken)
    {
        var catalog = ResourceCatalog.Empty;
        var facts = new List<OperationalFact>();

        IReadOnlyList<RepositoryHealth> repositories = [];
        try
        {
            var result = await health.ReadAsync(cancellationToken);
            repositories = result.Report?.Repositories ?? [];

            // A stale report is worse than no report if nobody says so: it describes a machine as it
            // was, in the present tense.
            if (result.State is HealthStoreState.Stale or HealthStoreState.Corrupt)
            {
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

        return _builder.Build(catalog, facts).Render();
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
    /// its own screens, and an assistant that restated them in its own words would be answering from
    /// a second, slightly different account of what is wrong.
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
