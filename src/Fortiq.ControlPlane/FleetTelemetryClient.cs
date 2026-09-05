using Fortiq.Monitoring;

namespace Fortiq.ControlPlane;

public interface ISequenceCounter
{
    long NextSequence();
}

public sealed class InMemorySequenceCounter : ISequenceCounter
{
    private long _current;

    public InMemorySequenceCounter(long initial = 0)
    {
        _current = initial;
    }

    public long NextSequence() => Interlocked.Increment(ref _current);
}

/// <summary>
/// Endpoint client component that transforms local health reports (from Fortiq.Monitoring) into
/// cryptographically signed, metadata-only fleet telemetry envelopes for transmission to the Control Plane.
/// </summary>
public sealed class FleetTelemetryClient
{
    private readonly HostIdentity _identity;
    private readonly DeviceKey _key;
    private readonly ISequenceCounter _sequence;
    private readonly Func<DateTimeOffset> _clock;

    public FleetTelemetryClient(
        HostIdentity identity,
        DeviceKey key,
        ISequenceCounter sequence,
        Func<DateTimeOffset>? clock = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SignedFleetEnvelope CreateTelemetryEnvelope(HealthReport localReport)
    {
        ArgumentNullException.ThrowIfNull(localReport);

        var now = _clock();
        var sequenceNumber = _sequence.NextSequence();

        var repos = new List<TelemetryRepositoryFacts>();
        foreach (var r in localReport.Repositories)
        {
            long? backupAge = r.Facts.LastBackupAt.HasValue
                ? (long)Math.Max(0, (now - r.Facts.LastBackupAt.Value).TotalSeconds)
                : null;

            long? restoreAge = r.Facts.LastProvenRestoreAt.HasValue
                ? (long)Math.Max(0, (now - r.Facts.LastProvenRestoreAt.Value).TotalSeconds)
                : null;

            long? checkAge = r.Facts.LastHealthyCheckAt.HasValue
                ? (long)Math.Max(0, (now - r.Facts.LastHealthyCheckAt.Value).TotalSeconds)
                : null;

            var protection = r.Facts.StorageImmutable ? "Immutable" : "Mutable";

            var anomalies = r.Findings.Count > 0
                ? r.Findings.Select(f => f.ToString()).ToArray()
                : null;

            repos.Add(new TelemetryRepositoryFacts(
                r.RepositoryId,
                r.Verdict,
                protection,
                backupAge,
                restoreAge,
                checkAge,
                LatestReceiptHash: null,
                anomalies));
        }

        var payload = new FleetTelemetryPayload(
            _identity.TenantId,
            _identity.HostId,
            sequenceNumber,
            now,
            localReport.Worst,
            repos);

        return SignedFleetEnvelope.Sign(payload, _key);
    }
}
