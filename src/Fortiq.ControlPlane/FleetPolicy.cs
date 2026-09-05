using Fortiq.Monitoring;

namespace Fortiq.ControlPlane;

public enum PolicyViolationType
{
    TenantMismatch,
    RpoExceeded,
    RestoreProofSlaExceeded,
    RecoverabilityUnproven,
    StorageImmutabilityLost
}

public sealed record FleetPolicyViolation(
    PolicyViolationType Type,
    string Message,
    string? RepositoryId = null);

/// <summary>
/// A signed protection and SLA policy issued by a tenant administrator for endpoints (Schema: fortiq.fleet-policy v1).
/// Defines mandatory RPO, restore proof drill SLAs, and storage protection constraints.
/// </summary>
public sealed record FleetPolicyDocument(
    string TenantId,
    string PolicyId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int MaxBackupAgeHours = 24,
    int MaxRestoreProofAgeDays = 7,
    bool RequireStorageImmutability = true,
    string Schema = FleetPolicyDocument.PolicySchema,
    int Version = FleetPolicyDocument.PolicyVersion)
{
    public const string PolicySchema = "fortiq.fleet-policy";
    public const int PolicyVersion = 1;

    public bool IsActive(DateTimeOffset at) => at >= IssuedAt && at <= ExpiresAt;

    public IReadOnlyList<FleetPolicyViolation> Evaluate(FleetTelemetryPayload telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var violations = new List<FleetPolicyViolation>();

        if (!string.Equals(TenantId, telemetry.TenantId, StringComparison.Ordinal))
        {
            violations.Add(new FleetPolicyViolation(
                PolicyViolationType.TenantMismatch,
                $"Telemetry tenant '{telemetry.TenantId}' does not match policy tenant '{TenantId}'."));
            return violations;
        }

        foreach (var repo in telemetry.Repositories)
        {
            if (repo.LastBackupAgeSeconds.HasValue)
            {
                var maxBackupSeconds = (long)MaxBackupAgeHours * 3600;
                if (repo.LastBackupAgeSeconds.Value > maxBackupSeconds)
                {
                    violations.Add(new FleetPolicyViolation(
                        PolicyViolationType.RpoExceeded,
                        $"Backup age {repo.LastBackupAgeSeconds.Value / 3600}h exceeds policy RPO threshold {MaxBackupAgeHours}h.",
                        repo.RepositoryId));
                }
            }
            else
            {
                violations.Add(new FleetPolicyViolation(
                    PolicyViolationType.RpoExceeded,
                    "Repository has never completed a successful backup.",
                    repo.RepositoryId));
            }

            if (repo.Verdict != HealthVerdict.Recoverable)
            {
                violations.Add(new FleetPolicyViolation(
                    PolicyViolationType.RecoverabilityUnproven,
                    $"Repository recoverability verdict is '{repo.Verdict}', expected 'Recoverable'.",
                    repo.RepositoryId));
            }

            if (repo.LastProvenRestoreAgeSeconds.HasValue)
            {
                var maxRestoreSeconds = (long)MaxRestoreProofAgeDays * 86400;
                if (repo.LastProvenRestoreAgeSeconds.Value > maxRestoreSeconds)
                {
                    violations.Add(new FleetPolicyViolation(
                        PolicyViolationType.RestoreProofSlaExceeded,
                        $"Last proven restore was {repo.LastProvenRestoreAgeSeconds.Value / 86400} days ago, exceeding SLA of {MaxRestoreProofAgeDays} days.",
                        repo.RepositoryId));
                }
            }
            else
            {
                violations.Add(new FleetPolicyViolation(
                    PolicyViolationType.RestoreProofSlaExceeded,
                    "Repository has never established a verified restore proof.",
                    repo.RepositoryId));
            }

            if (RequireStorageImmutability && !string.Equals(repo.StorageProtection, "Immutable", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new FleetPolicyViolation(
                    PolicyViolationType.StorageImmutabilityLost,
                    $"Storage protection is '{repo.StorageProtection}', policy mandates 'Immutable'.",
                    repo.RepositoryId));
            }
        }

        return violations;
    }
}
