using Fortiq.Monitoring;

namespace Fortiq.ControlPlane;

/// <summary>
/// Telemetry facts for a single managed repository. Contains strictly metadata, verdicts, ages,
/// and cryptographic receipt hashes. Never carries file names, directory paths, or secret material.
/// </summary>
public sealed record TelemetryRepositoryFacts(
    string RepositoryId,
    HealthVerdict Verdict,
    string StorageProtection,
    long? LastBackupAgeSeconds = null,
    long? LastProvenRestoreAgeSeconds = null,
    long? LastCheckAgeSeconds = null,
    string? LatestReceiptHash = null,
    IReadOnlyList<string>? Anomalies = null);

/// <summary>
/// Structured telemetry payload sent from an endpoint to the Control Plane (Schema: fortiq.fleet-telemetry v1).
/// Adheres strictly to the Metadata-Only model specified in Spec 18 and ADR-010.
/// </summary>
public sealed record FleetTelemetryPayload(
    string TenantId,
    string HostId,
    long SequenceNumber,
    DateTimeOffset GeneratedAt,
    HealthVerdict WorstVerdict,
    IReadOnlyList<TelemetryRepositoryFacts> Repositories,
    string Schema = FleetTelemetryPayload.TelemetrySchema,
    int Version = FleetTelemetryPayload.TelemetryVersion)
{
    public const string TelemetrySchema = "fortiq.fleet-telemetry";
    public const int TelemetryVersion = 1;
}

/// <summary>
/// Validates that telemetry payloads strictly adhere to privacy and zero-secret invariants.
/// </summary>
public static class TelemetryPrivacyValidator
{
    private static readonly string[] ForbiddenTokens =
    {
        "password", "secret", "mnemonic", "seed", "token", "privatekey", "bearer"
    };

    /// <summary>
    /// Checks that the telemetry payload contains no forbidden secrets, file paths, or private material.
    /// </summary>
    public static void Validate(FleetTelemetryPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(payload.TenantId))
            throw new ArgumentException("TenantId is required.", nameof(payload));
        if (string.IsNullOrWhiteSpace(payload.HostId))
            throw new ArgumentException("HostId is required.", nameof(payload));
        if (payload.SequenceNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(payload), "SequenceNumber must be positive.");

        foreach (var repo in payload.Repositories)
        {
            ValidateString(repo.RepositoryId, "RepositoryId");
            ValidateString(repo.StorageProtection, "StorageProtection");

            if (repo.LatestReceiptHash is not null)
            {
                ValidateString(repo.LatestReceiptHash, "LatestReceiptHash");
                if (repo.LatestReceiptHash.Length != 64 || !IsHexString(repo.LatestReceiptHash))
                {
                    throw new InvalidDataException($"LatestReceiptHash '{repo.LatestReceiptHash}' must be a 64-character SHA-256 hexadecimal digest.");
                }
            }

            if (repo.Anomalies is not null)
            {
                foreach (var anomaly in repo.Anomalies)
                {
                    ValidateString(anomaly, "Anomaly");
                }
            }
        }
    }

    private static void ValidateString(string value, string fieldName)
    {
        if (value.Contains(":\\", StringComparison.Ordinal) ||
            value.Contains(":/", StringComparison.Ordinal) ||
            value.StartsWith('/') ||
            value.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Privacy invariant violated in {fieldName}: file paths must not transit control plane telemetry ('{value}').");
        }

        foreach (var token in ForbiddenTokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Privacy invariant violated in {fieldName}: potential secret token detected ('{token}').");
            }
        }
    }

    private static bool IsHexString(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }
}
