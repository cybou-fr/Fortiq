using Fortiq.Monitoring;

namespace Fortiq.ControlPlane.Tests;

public sealed class FleetIngestionAndPolicyTests
{
    private readonly InMemoryFleetHostRegistry _registry = new();
    private readonly DateTimeOffset _currentTime = DateTimeOffset.UtcNow;

    private async Task<(HostIdentity Host, DeviceKey Key)> SetupEnrolledHostAsync(string tenantId = "tenant-prod", string hostId = "host-srv01")
    {
        var key = DeviceKey.Generate();
        var host = new HostIdentity(
            tenantId,
            hostId,
            "backup-node-01",
            "win-x64",
            "1.0.0",
            key.ExportPublicKeyHex(),
            _currentTime);

        await _registry.RegisterHostAsync(host);
        return (host, key);
    }

    [Fact]
    public async Task IngestionUnenrolledHostIsRejected()
    {
        var engine = new FleetIngestionEngine(_registry, () => _currentTime);
        using var key = DeviceKey.Generate();

        var payload = new FleetTelemetryPayload(
            "unknown-tenant",
            "unknown-host",
            1,
            _currentTime,
            HealthVerdict.Recoverable,
            []);

        var result = await engine.IngestAsync(SignedFleetEnvelope.Sign(payload, key));

        Assert.False(result.Success);
        Assert.Equal(IngestionFailureReason.HostNotEnrolled, result.FailureReason);
    }

    [Fact]
    public async Task IngestionTenantMismatchIsEnforced()
    {
        var (host, key) = await SetupEnrolledHostAsync("tenant-prod", "host-01");
        var engine = new FleetIngestionEngine(_registry, () => _currentTime);

        // Host attempts to submit telemetry for a different tenant
        var payload = new FleetTelemetryPayload(
            "tenant-dev",
            host.HostId,
            1,
            _currentTime,
            HealthVerdict.Recoverable,
            []);

        var result = await engine.IngestAsync(SignedFleetEnvelope.Sign(payload, key));

        Assert.False(result.Success);
        Assert.Equal(IngestionFailureReason.HostNotEnrolled, result.FailureReason);
    }

    [Fact]
    public async Task IngestionKeyMismatchIsRejected()
    {
        var (host, _) = await SetupEnrolledHostAsync("tenant-prod", "host-01");
        var engine = new FleetIngestionEngine(_registry, () => _currentTime);

        // Rogue key tries to sign on behalf of host-01
        using var rogueKey = DeviceKey.Generate();
        var payload = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            1,
            _currentTime,
            HealthVerdict.Recoverable,
            []);

        var result = await engine.IngestAsync(SignedFleetEnvelope.Sign(payload, rogueKey));

        Assert.False(result.Success);
        Assert.Equal(IngestionFailureReason.KeyMismatch, result.FailureReason);
    }

    [Fact]
    public async Task PolicyEvaluationDetectsRpoAndSlaViolations()
    {
        var (host, key) = await SetupEnrolledHostAsync();
        var engine = new FleetIngestionEngine(_registry, () => _currentTime);

        var policy = new FleetPolicyDocument(
            host.TenantId,
            "policy-gold",
            _currentTime.AddDays(-1),
            _currentTime.AddDays(30),
            MaxBackupAgeHours: 24,
            MaxRestoreProofAgeDays: 7,
            RequireStorageImmutability: true);

        // Repo 1 violates RPO (backup is 30h old) and is Unproven
        // Repo 2 has lost immutability
        var payload = new FleetTelemetryPayload(
            host.TenantId,
            host.HostId,
            1,
            _currentTime,
            HealthVerdict.AtRisk,
            [
                new TelemetryRepositoryFacts(
                    "repo-1",
                    HealthVerdict.Unproven,
                    "Immutable",
                    LastBackupAgeSeconds: 30 * 3600,
                    LastProvenRestoreAgeSeconds: 10 * 86400,
                    LatestReceiptHash: new string('b', 64)),
                new TelemetryRepositoryFacts(
                    "repo-2",
                    HealthVerdict.AtRisk,
                    "Mutable",
                    LastBackupAgeSeconds: 3600,
                    LastProvenRestoreAgeSeconds: 3600,
                    LatestReceiptHash: new string('c', 64))
            ]);

        var result = await engine.IngestAsync(SignedFleetEnvelope.Sign(payload, key), policy);

        Assert.True(result.Success);
        Assert.NotNull(result.Status);

        var violations = result.Status.Violations;
        Assert.Contains(violations, v => v.Type == PolicyViolationType.RpoExceeded && v.RepositoryId == "repo-1");
        Assert.Contains(violations, v => v.Type == PolicyViolationType.RestoreProofSlaExceeded && v.RepositoryId == "repo-1");
        Assert.Contains(violations, v => v.Type == PolicyViolationType.RecoverabilityUnproven && v.RepositoryId == "repo-1");
        Assert.Contains(violations, v => v.Type == PolicyViolationType.StorageImmutabilityLost && v.RepositoryId == "repo-2");
    }

    [Fact]
    public void PrivacyValidatorRejectsFilePathsAndSecretTokens()
    {
        var invalidPathPayload = new FleetTelemetryPayload(
            "tenant-1",
            "host-1",
            1,
            DateTimeOffset.UtcNow,
            HealthVerdict.Recoverable,
            [
                new TelemetryRepositoryFacts(
                    "C:\\Users\\Admin\\Documents",
                    HealthVerdict.Recoverable,
                    "Immutable")
            ]);

        Assert.Throws<InvalidOperationException>(() => TelemetryPrivacyValidator.Validate(invalidPathPayload));

        var invalidSecretPayload = new FleetTelemetryPayload(
            "tenant-1",
            "host-1",
            1,
            DateTimeOffset.UtcNow,
            HealthVerdict.Recoverable,
            [
                new TelemetryRepositoryFacts(
                    "repo-1",
                    HealthVerdict.Recoverable,
                    "Immutable",
                    Anomalies: ["detected password in header"])
            ]);

        Assert.Throws<InvalidOperationException>(() => TelemetryPrivacyValidator.Validate(invalidSecretPayload));
    }

    [Fact]
    public void FleetMatrixAggregatesCorrectlyAcrossMultipleHosts()
    {
        var status1 = new FleetHostStatus("tenant-1", "host-1", "node-1", DateTimeOffset.UtcNow, 1, HealthVerdict.Recoverable, [], []);
        var status2 = new FleetHostStatus("tenant-1", "host-2", "node-2", DateTimeOffset.UtcNow, 1, HealthVerdict.Unproven, [new FleetPolicyViolation(PolicyViolationType.RecoverabilityUnproven, "test")], []);
        var status3 = new FleetHostStatus("tenant-1", "host-3", "node-3", DateTimeOffset.UtcNow, 1, HealthVerdict.AtRisk, [new FleetPolicyViolation(PolicyViolationType.RpoExceeded, "test")], []);
        var otherTenantStatus = new FleetHostStatus("tenant-2", "host-4", "node-4", DateTimeOffset.UtcNow, 1, HealthVerdict.Recoverable, [], []);

        var matrix = FleetIngestionEngine.AggregateMatrix("tenant-1", [status1, status2, status3, otherTenantStatus]);

        Assert.Equal("tenant-1", matrix.TenantId);
        Assert.Equal(3, matrix.TotalHosts);
        Assert.Equal(1, matrix.RecoverableHosts);
        Assert.Equal(1, matrix.UnprovenHosts);
        Assert.Equal(1, matrix.AtRiskHosts);
        Assert.Equal(2, matrix.TotalViolations);
    }
}
