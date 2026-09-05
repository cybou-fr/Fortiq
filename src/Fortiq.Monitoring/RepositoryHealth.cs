namespace Fortiq.Monitoring;

/// <summary>
/// What the storage said when it was last asked, which is a different fact from what it promised
/// when the repository was created.
/// </summary>
/// <remarks>
/// The distinction is the point. Reporting the protection recorded at provisioning time as though it
/// were current means a bucket whose retention was lifted last week still shows as immutable - and
/// lifting that retention is precisely the first move of somebody preparing to delete the backups.
/// </remarks>
public enum StorageProtectionStatus
{
    /// <summary>Nobody has asked the storage. Not a claim in either direction.</summary>
    Unknown,

    /// <summary>The storage was asked and keeps what is written to it.</summary>
    Immutable,

    /// <summary>The storage was asked and promises nothing.</summary>
    NotImmutable
}

/// <summary>What is known about a repository, as facts rather than conclusions.</summary>
public sealed record RepositoryFacts(
    string RepositoryId,
    string? ScheduleId,
    DateTimeOffset? LastBackupAt,
    DateTimeOffset? LastHealthyCheckAt,
    DateTimeOffset? LastProvenRestoreAt,
    bool KitPresent,
    /// <summary>What the kit recorded the storage promising when the repository was created.</summary>
    bool StorageImmutable,
    string? LastFailure = null,
    /// <summary>What the storage says today. Defaults to unknown: nothing is claimed unasked.</summary>
    StorageProtectionStatus StorageProtectionNow = StorageProtectionStatus.Unknown,
    /// <summary>Failure or anomaly detected during cryptographic audit ledger verification.</summary>
    string? AuditLedgerFailure = null);

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
    public static RepositoryHealth Assess(
        RepositoryFacts facts,
        DateTimeOffset now,
        HealthThresholds? thresholds = null,
        IReadOnlyList<BackupAnomaly>? anomalies = null)
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

        if (facts.AuditLedgerFailure is { Length: > 0 } ledgerAnomaly)
        {
            findings.Add(new HealthFinding("audit-ledger-tampered", ledgerAnomaly));
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

        // Storage protection is judged on what the storage says now, and only falls back to what it
        // promised at provisioning when nobody could ask. A guarantee recorded months ago is not
        // evidence about today.
        switch (facts.StorageProtectionNow)
        {
            case StorageProtectionStatus.NotImmutable when facts.StorageImmutable:
                findings.Add(new HealthFinding(
                    "storage-protection-lost",
                    "This repository was created on storage that kept what was written to it, and that "
                    + "storage no longer does. Removing retention is the first step towards deleting the backups."));
                break;

            case StorageProtectionStatus.NotImmutable:
                findings.Add(new HealthFinding(
                    "storage-not-immutable",
                    "The storage holding this repository does not keep what is written to it."));
                break;

            case StorageProtectionStatus.Unknown when facts.StorageImmutable:
                findings.Add(new HealthFinding(
                    "storage-protection-unknown",
                    "The storage could not be asked what it protects. It promised to keep what was "
                    + "written to it when this repository was created; whether it still does is unverified."));
                break;

            case StorageProtectionStatus.Unknown:
                findings.Add(new HealthFinding(
                    "storage-not-immutable",
                    "The storage holding this repository does not keep what is written to it."));
                break;

            default:
                break;
        }

        // The verdict is settled before anomalies are added. An unusual backup is not a reason to
        // say a repository cannot be recovered - the snapshots are still there and still restorable -
        // and letting it lower the verdict would mean a large but perfectly legitimate change made
        // Fortiq report a recovery problem that does not exist.
        var verdict = Verdict(findings);
        foreach (var anomaly in anomalies ?? [])
        {
            findings.Add(new HealthFinding("backup-unusual", anomaly.Detail));
        }

        return new RepositoryHealth(facts.RepositoryId, facts.ScheduleId, verdict, findings, facts);
    }

    private static HealthVerdict Verdict(List<HealthFinding> findings)
    {
        // A missing kit, a repository with no backup, a stale backup or a failed run are all things
        // that would hurt today; everything else means the backup exists but nothing has shown it
        // works.
        if (findings.Any(finding => finding.Code is "kit-missing" or "never-backed-up" or "backup-stale" or "last-run-failed" or "audit-ledger-tampered"))
        {
            return HealthVerdict.AtRisk;
        }

        return findings.Count == 0 ? HealthVerdict.Recoverable : HealthVerdict.Unproven;
    }
}
