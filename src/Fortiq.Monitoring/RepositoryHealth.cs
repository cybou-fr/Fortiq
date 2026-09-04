namespace Fortiq.Monitoring;

/// <summary>What is known about a repository, as facts rather than conclusions.</summary>
public sealed record RepositoryFacts(
    string RepositoryId,
    string? ScheduleId,
    DateTimeOffset? LastBackupAt,
    DateTimeOffset? LastHealthyCheckAt,
    DateTimeOffset? LastProvenRestoreAt,
    bool KitPresent,
    bool StorageImmutable,
    string? LastFailure = null);

/// <summary>How old each kind of evidence may be before it stops counting.</summary>
public sealed record HealthThresholds(
    TimeSpan BackupAge,
    TimeSpan CheckAge,
    TimeSpan RestoreProofAge)
{
    /// <summary>
    /// Daily backups, a weekly integrity check, and a restore proven within the last month. The last
    /// one is the point: a backup nobody has restored is a belief, not a capability.
    /// </summary>
    public static HealthThresholds Default { get; } = new(
        TimeSpan.FromDays(1.5),
        TimeSpan.FromDays(8),
        TimeSpan.FromDays(31));
}

/// <summary>
/// What Fortiq is willing to claim about a repository. Deliberately not a traffic light over "did
/// the job run": a job that ran says nothing about whether the data comes back.
/// </summary>
public enum HealthVerdict
{
    /// <summary>Backed up recently, checked recently, and a restore has been proven recently.</summary>
    Recoverable,

    /// <summary>Backed up, but something that would prove recoverability is missing or stale.</summary>
    Unproven,

    /// <summary>Something is wrong that would stop a recovery today.</summary>
    AtRisk
}

public sealed record HealthFinding(string Code, string Detail);

public sealed record RepositoryHealth(
    string RepositoryId,
    string? ScheduleId,
    HealthVerdict Verdict,
    IReadOnlyList<HealthFinding> Findings,
    RepositoryFacts Facts);

public sealed record HealthReport(DateTimeOffset ProducedAt, IReadOnlyList<RepositoryHealth> Repositories)
{
    public const string Schema = "fortiq.health-report";
    public const int SchemaVersion = 1;

    /// <summary>The worst verdict present, which is what an alert should act on.</summary>
    public HealthVerdict Worst => Repositories.Count == 0
        ? HealthVerdict.Unproven
        : Repositories.Max(repository => repository.Verdict);
}

/// <summary>
/// Turns facts into a verdict. Pure, so the awkward judgements - what counts as stale, what counts
/// as unrecoverable - are stated once and can be argued with.
/// </summary>
public static class HealthAssessor
{
    public static RepositoryHealth Assess(RepositoryFacts facts, DateTimeOffset now, HealthThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var limits = thresholds ?? HealthThresholds.Default;
        var findings = new List<HealthFinding>();

        // At risk: things that would stop a recovery today.
        if (!facts.KitPresent)
        {
            findings.Add(new HealthFinding(
                "kit-missing",
                "No recovery kit was found, so this repository cannot be opened on another machine."));
        }

        if (facts.LastBackupAt is null)
        {
            findings.Add(new HealthFinding("never-backed-up", "This repository holds no backup yet."));
        }
        else if (now - facts.LastBackupAt > limits.BackupAge)
        {
            findings.Add(new HealthFinding(
                "backup-stale",
                $"The last backup was at {facts.LastBackupAt:O}, older than the {limits.BackupAge} this repository allows."));
        }

        if (facts.LastFailure is { Length: > 0 } failure)
        {
            findings.Add(new HealthFinding("last-run-failed", failure));
        }

        // Unproven: backed up, but nothing has demonstrated that it comes back.
        if (facts.LastHealthyCheckAt is null)
        {
            findings.Add(new HealthFinding("never-checked", "This repository has never passed an integrity check."));
        }
        else if (now - facts.LastHealthyCheckAt > limits.CheckAge)
        {
            findings.Add(new HealthFinding(
                "check-stale",
                $"The last healthy check was at {facts.LastHealthyCheckAt:O}."));
        }

        if (facts.LastProvenRestoreAt is null)
        {
            findings.Add(new HealthFinding(
                "restore-never-proven",
                "Nothing has ever been restored from this repository, so recovery is untested."));
        }
        else if (now - facts.LastProvenRestoreAt > limits.RestoreProofAge)
        {
            findings.Add(new HealthFinding(
                "restore-proof-stale",
                $"The last proven restore was at {facts.LastProvenRestoreAt:O}."));
        }

        if (!facts.StorageImmutable)
        {
            findings.Add(new HealthFinding(
                "storage-not-immutable",
                "The storage holding this repository does not keep what is written to it."));
        }

        return new RepositoryHealth(facts.RepositoryId, facts.ScheduleId, Verdict(findings), findings, facts);
    }

    private static HealthVerdict Verdict(List<HealthFinding> findings)
    {
        // A missing kit, a repository with no backup, a stale backup or a failed run are all things
        // that would hurt today; everything else means the backup exists but nothing has shown it
        // works.
        if (findings.Any(finding => finding.Code is "kit-missing" or "never-backed-up" or "backup-stale" or "last-run-failed"))
        {
            return HealthVerdict.AtRisk;
        }

        return findings.Count == 0 ? HealthVerdict.Recoverable : HealthVerdict.Unproven;
    }
}
