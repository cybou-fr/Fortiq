using Fortiq.Monitoring;

namespace Fortiq.ControlPlane.Tests;

public sealed class FleetAntiReplayAndTimingTests
{
    private readonly InMemoryFleetHostRegistry _registry = new();
    private readonly DateTimeOffset _currentTime = DateTimeOffset.UtcNow;

    private FleetIngestionEngine CreateEngine(TimeSpan? clockSkew = null)
    {
        return new FleetIngestionEngine(_registry, () => _currentTime, clockSkew);
    }

    private async Task<(HostIdentity Host, DeviceKey Key)> SetupEnrolledHostAsync(string tenantId = "tenant-test", string hostId = "host-01")
    {
        var key = DeviceKey.Generate();
        var host = new HostIdentity(
            tenantId,
            hostId,
            "laptop-alice",
            "win-x64",
            "1.0.0",
            key.ExportPublicKeyHex(),
            _currentTime);

        await _registry.RegisterHostAsync(host);
        return (host, key);
    }

    [Fact]
    public async Task IngestionMonotonicSequenceAdvancesSuccessfully()
    {
        var (host, key) = await SetupEnrolledHostAsync();
        var engine = CreateEngine();

        for (long seq = 1; seq <= 3; seq++)
        {
            var payload = new FleetTelemetryPayload(
                host.TenantId,
                host.HostId,
                seq,
                _currentTime,
                HealthVerdict.Recoverable,
                []);

            var envelope = SignedFleetEnvelope.Sign(payload, key);
            var result = await engine.IngestAsync(envelope);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(IngestionFailureReason.None, result.FailureReason);
            Assert.NotNull(result.Status);
            Assert.Equal(seq, result.Status.SequenceNumber);
        }

        var lastSeq = await _registry.GetLastSequenceAsync(host.TenantId, host.HostId);
        Assert.Equal(3, lastSeq);
    }

    [Fact]
    public async Task IngestionReplayedSequenceIsRejected()
    {
        var (host, key) = await SetupEnrolledHostAsync();
        var engine = CreateEngine();

        var payload = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            1,
            _currentTime,
            HealthVerdict.Recoverable,
            []);

        var envelope = SignedFleetEnvelope.Sign(payload, key);

        // First attempt succeeds
        var res1 = await engine.IngestAsync(envelope);
        Assert.True(res1.Success);

        // Replay attempt fails
        var res2 = await engine.IngestAsync(envelope);
        Assert.False(res2.Success);
        Assert.Equal(IngestionFailureReason.SequenceReplayedOrOutdated, res2.FailureReason);
    }

    [Fact]
    public async Task IngestionOutdatedSequenceIsRejected()
    {
        var (host, key) = await SetupEnrolledHostAsync();
        var engine = CreateEngine();

        var payload2 = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            2,
            _currentTime,
            HealthVerdict.Recoverable,
            []);
        var res2 = await engine.IngestAsync(SignedFleetEnvelope.Sign(payload2, key));
        Assert.True(res2.Success);

        // Sequence 1 arrived after sequence 2 -> rejected
        var payload1 = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            1,
            _currentTime,
            HealthVerdict.Recoverable,
            []);
        var res1 = await engine.IngestAsync(SignedFleetEnvelope.Sign(payload1, key));
        Assert.False(res1.Success);
        Assert.Equal(IngestionFailureReason.SequenceReplayedOrOutdated, res1.FailureReason);
    }

    [Fact]
    public async Task IngestionClockSkewExceededIsRejected()
    {
        var (host, key) = await SetupEnrolledHostAsync();
        var engine = CreateEngine(TimeSpan.FromMinutes(5));

        // Telemetry generated 15 minutes ago
        var pastPayload = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            1,
            _currentTime.AddMinutes(-15),
            HealthVerdict.Recoverable,
            []);

        var resPast = await engine.IngestAsync(SignedFleetEnvelope.Sign(pastPayload, key));
        Assert.False(resPast.Success);
        Assert.Equal(IngestionFailureReason.ClockSkewExceeded, resPast.FailureReason);

        // Telemetry generated 15 minutes in the future
        var futurePayload = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            1,
            _currentTime.AddMinutes(15),
            HealthVerdict.Recoverable,
            []);

        var resFuture = await engine.IngestAsync(SignedFleetEnvelope.Sign(futurePayload, key));
        Assert.False(resFuture.Success);
        Assert.Equal(IngestionFailureReason.ClockSkewExceeded, resFuture.FailureReason);
    }
}
