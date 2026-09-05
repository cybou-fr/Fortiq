using Fortiq.Monitoring;

namespace Fortiq.ControlPlane;

public enum IngestionFailureReason
{
    None,
    MalformedPayload,
    PrivacyViolation,
    HostNotEnrolled,
    KeyMismatch,
    InvalidSignature,
    ClockSkewExceeded,
    SequenceReplayedOrOutdated
}

public sealed record IngestionResult(
    bool Success,
    IngestionFailureReason FailureReason,
    string? ErrorMessage,
    FleetHostStatus? Status);

public sealed record FleetHostStatus(
    string TenantId,
    string HostId,
    string Hostname,
    DateTimeOffset LastSeenAt,
    long SequenceNumber,
    HealthVerdict WorstVerdict,
    IReadOnlyList<FleetPolicyViolation> Violations,
    IReadOnlyList<TelemetryRepositoryFacts> Repositories);

public sealed record FleetHealthMatrix(
    string TenantId,
    int TotalHosts,
    int RecoverableHosts,
    int UnprovenHosts,
    int AtRiskHosts,
    int TotalViolations,
    DateTimeOffset CalculatedAt);

/// <summary>
/// The central ingestion engine processing outbound telemetry from endpoints. Validates cryptographic
/// signatures, enforces anti-replay guards, validates policy compliance, and aggregates fleet health.
/// </summary>
public sealed class FleetIngestionEngine
{
    private readonly IFleetHostRegistry _registry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _maxClockSkew;

    public FleetIngestionEngine(
        IFleetHostRegistry registry,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? maxClockSkew = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maxClockSkew = maxClockSkew ?? TimeSpan.FromMinutes(10);
    }

    public async Task<IngestionResult> IngestAsync(
        SignedFleetEnvelope envelope,
        FleetPolicyDocument? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        FleetTelemetryPayload payload;
        try
        {
            payload = envelope.Unpack<FleetTelemetryPayload>();
        }
        catch (Exception ex)
        {
            return new IngestionResult(false, IngestionFailureReason.MalformedPayload, $"Failed to parse payload: {ex.Message}", null);
        }

        try
        {
            TelemetryPrivacyValidator.Validate(payload);
        }
        catch (Exception ex)
        {
            return new IngestionResult(false, IngestionFailureReason.PrivacyViolation, $"Privacy invariant violated: {ex.Message}", null);
        }

        var host = await _registry.GetHostAsync(payload.TenantId, payload.HostId, cancellationToken);
        if (host is null)
        {
            return new IngestionResult(false, IngestionFailureReason.HostNotEnrolled, $"Host '{payload.HostId}' is not enrolled in tenant '{payload.TenantId}'.", null);
        }

        if (!string.Equals(host.KeyId, envelope.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            return new IngestionResult(false, IngestionFailureReason.KeyMismatch, $"Envelope key '{envelope.KeyId}' does not match host registered key '{host.KeyId}'.", null);
        }

        if (!envelope.Verify(host.PublicKeyHex))
        {
            return new IngestionResult(false, IngestionFailureReason.InvalidSignature, "Digital signature verification failed.", null);
        }

        var now = _clock();
        var skew = (now - payload.GeneratedAt).Duration();
        if (skew > _maxClockSkew)
        {
            return new IngestionResult(false, IngestionFailureReason.ClockSkewExceeded, $"Telemetry timestamp {payload.GeneratedAt:u} is outside allowed clock skew window (skew: {skew.TotalSeconds:N0}s).", null);
        }

        var lastSeq = await _registry.GetLastSequenceAsync(payload.TenantId, payload.HostId, cancellationToken);
        if (payload.SequenceNumber <= lastSeq)
        {
            return new IngestionResult(false, IngestionFailureReason.SequenceReplayedOrOutdated, $"Sequence {payload.SequenceNumber} was already seen or is outdated (last: {lastSeq}).", null);
        }

        var advanced = await _registry.TryAdvanceSequenceAsync(
            payload.TenantId,
            payload.HostId,
            lastSeq,
            payload.SequenceNumber,
            now,
            cancellationToken);

        if (!advanced)
        {
            return new IngestionResult(false, IngestionFailureReason.SequenceReplayedOrOutdated, "Concurrent sequence race detected during sequence advancement.", null);
        }

        var violations = policy is not null && policy.IsActive(now)
            ? policy.Evaluate(payload)
            : Array.Empty<FleetPolicyViolation>();

        var status = new FleetHostStatus(
            payload.TenantId,
            payload.HostId,
            host.Hostname,
            now,
            payload.SequenceNumber,
            payload.WorstVerdict,
            violations,
            payload.Repositories);

        return new IngestionResult(true, IngestionFailureReason.None, null, status);
    }

    public static FleetHealthMatrix AggregateMatrix(string tenantId, IReadOnlyList<FleetHostStatus> hostStatuses, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(hostStatuses);

        var tenantHosts = hostStatuses.Where(h => string.Equals(h.TenantId, tenantId, StringComparison.Ordinal)).ToArray();
        var recoverable = tenantHosts.Count(h => h.WorstVerdict == HealthVerdict.Recoverable);
        var unproven = tenantHosts.Count(h => h.WorstVerdict == HealthVerdict.Unproven);
        var atRisk = tenantHosts.Count(h => h.WorstVerdict == HealthVerdict.AtRisk);
        var totalViolations = tenantHosts.Sum(h => h.Violations.Count);

        return new FleetHealthMatrix(
            tenantId,
            tenantHosts.Length,
            recoverable,
            unproven,
            atRisk,
            totalViolations,
            now ?? DateTimeOffset.UtcNow);
    }
}
