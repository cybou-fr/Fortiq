using Fortiq.Monitoring;

namespace Fortiq.ControlPlane.Tests;

public sealed class FleetTelemetryCryptographyTests
{
    [Fact]
    public void DeviceKeyGeneratesValidP256KeyPairAndKeyId()
    {
        using var key = DeviceKey.Generate();

        Assert.Equal(64, key.KeyId.Length);
        var pubHex = key.ExportPublicKeyHex();
        Assert.NotEmpty(pubHex);

        var derivedKeyId = DeviceKey.ComputeKeyIdFromPublicKey(pubHex);
        Assert.Equal(key.KeyId, derivedKeyId);
    }

    [Fact]
    public void SignedFleetEnvelopeValidSignatureVerifiesSuccessfully()
    {
        using var key = DeviceKey.Generate();
        var payload = new FleetTelemetryPayload(
            "tenant-alpha",
            "host-001",
            1,
            DateTimeOffset.UtcNow,
            HealthVerdict.Recoverable,
            [
                new TelemetryRepositoryFacts(
                    "repo-1",
                    HealthVerdict.Recoverable,
                    "Immutable",
                    LastBackupAgeSeconds: 300,
                    LastProvenRestoreAgeSeconds: 1200,
                    LastCheckAgeSeconds: 600,
                    LatestReceiptHash: new string('a', 64))
            ]);

        var envelope = SignedFleetEnvelope.Sign(payload, key);

        Assert.Equal(key.KeyId, envelope.KeyId);
        Assert.True(envelope.Verify(key.ExportPublicKeyHex()));

        var unpacked = envelope.Unpack<FleetTelemetryPayload>();
        Assert.Equal(payload.TenantId, unpacked.TenantId);
        Assert.Equal(payload.HostId, unpacked.HostId);
        Assert.Equal(payload.SequenceNumber, unpacked.SequenceNumber);
        Assert.Single(unpacked.Repositories);
    }

    [Fact]
    public void SignedFleetEnvelopePayloadTamperingFailsVerification()
    {
        using var key = DeviceKey.Generate();
        var payload = new FleetTelemetryPayload(
            "tenant-alpha",
            "host-001",
            1,
            DateTimeOffset.UtcNow,
            HealthVerdict.Recoverable,
            []);

        var envelope = SignedFleetEnvelope.Sign(payload, key);

        // Tamper with payload JSON
        var tamperedEnvelope = envelope with
        {
            PayloadJson = envelope.PayloadJson.Replace("tenant-alpha", "tenant-beta", StringComparison.Ordinal)
        };

        Assert.False(tamperedEnvelope.Verify(key.ExportPublicKeyHex()));
    }

    [Fact]
    public void SignedFleetEnvelopeWrongPublicKeyFailsVerification()
    {
        using var key1 = DeviceKey.Generate();
        using var key2 = DeviceKey.Generate();

        var payload = new FleetTelemetryPayload(
            "tenant-alpha",
            "host-001",
            1,
            DateTimeOffset.UtcNow,
            HealthVerdict.Recoverable,
            []);

        var envelope = SignedFleetEnvelope.Sign(payload, key1);

        // Verifying key1's envelope with key2's public key must fail
        Assert.False(envelope.Verify(key2.ExportPublicKeyHex()));
    }

    [Fact]
    public void CanonicalJsonDifferentKeyOrderProducesIdenticalEncoding()
    {
        var json1 = "{\"b\": 2, \"a\": 1}";
        var json2 = "{\"a\": 1, \"b\": 2}";

        var bytes1 = CanonicalJson.Encode(json1);
        var bytes2 = CanonicalJson.Encode(json2);

        Assert.Equal(bytes1, bytes2);
    }
}
