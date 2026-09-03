using System.Formats.Cbor;
using System.Security.Cryptography;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

/// <summary>
/// ADR-002 envelope behaviour: a round trip that survives serialization, a decoder that rejects
/// malformed input, and failures that are indistinguishable from one another.
/// </summary>
public sealed class KeyEnvelopeTests
{
    private static readonly byte[] RepositoryId = [.. Enumerable.Range(0, 32).Select(index => (byte)index)];
    private static readonly byte[] RecoveryEntropy = [.. Enumerable.Range(0, 32).Select(index => (byte)(index + 100))];
    private static readonly byte[] EngineUnlockSecret = [.. Enumerable.Range(0, 32).Select(index => (byte)(index * 3))];

    [Fact]
    public void WrappedSecretSurvivesASerializationRoundTrip()
    {
        var encoded = KeyEnvelopeCodec.Encode(WrapSecret());

        var decoded = KeyEnvelopeCodec.Decode(encoded);
        using var lease = RecoverySecretEnvelope.Unwrap(decoded, RepositoryId, RecoveryEntropy);
        var recovered = new byte[lease.Length];
        lease.CopyTo(recovered);

        Assert.Equal(EngineUnlockSecret, recovered);
        Assert.Equal(RecoverySecretEnvelope.SuiteId, decoded.Suite);
        Assert.Equal(KeyEnvelopeV1.EngineUnlockPurpose, decoded.Purpose);
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        var envelope = WrapSecret();

        Assert.Equal(KeyEnvelopeCodec.Encode(envelope), KeyEnvelopeCodec.Encode(envelope));
    }

    [Fact]
    public void TheEnvelopeNeverContainsThePlaintextSecret()
    {
        var encoded = KeyEnvelopeCodec.Encode(WrapSecret());

        Assert.DoesNotContain(Convert.ToHexStringLower(EngineUnlockSecret), Convert.ToHexStringLower(encoded), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(RecoveryEntropy), Convert.ToHexStringLower(encoded), StringComparison.Ordinal);
    }

    [Fact]
    public void WrongRecoveryEntropyFailsAsUnlockFailed()
    {
        var envelope = WrapSecret();
        var wrong = RecoveryEntropy.ToArray();
        wrong[0] ^= 0x01;

        Assert.Throws<UnlockFailedException>(() => RecoverySecretEnvelope.Unwrap(envelope, RepositoryId, wrong));
    }

    [Fact]
    public void AnEnvelopeOfAnotherRepositoryDoesNotOpen()
    {
        var envelope = WrapSecret();
        var otherRepository = RepositoryId.ToArray();
        otherRepository[31] ^= 0x01;

        Assert.Throws<UnlockFailedException>(
            () => RecoverySecretEnvelope.Unwrap(envelope, otherRepository, RecoveryEntropy));
    }

    [Theory]
    [InlineData("createdAt")]
    [InlineData("envelopeId")]
    public void ModifyingAnAuthenticatedFieldBreaksUnwrapping(string field)
    {
        var envelope = WrapSecret();
        var tampered = field == "createdAt"
            ? envelope with { CreatedAt = envelope.CreatedAt.AddSeconds(1) }
            : envelope with { EnvelopeId = RandomNumberGenerator.GetBytes(KeyEnvelopeV1.EnvelopeIdSize) };

        Assert.Throws<UnlockFailedException>(() => RecoverySecretEnvelope.Unwrap(tampered, RepositoryId, RecoveryEntropy));
    }

    [Fact]
    public void AnUnknownSuiteIsRefusedInsteadOfGuessed()
    {
        var envelope = WrapSecret() with { Suite = "recovery-entropy-argon2id-v9" };

        // A recovery tool that meets a newer suite must say so, not attempt the parameters it knows.
        Assert.Throws<InvalidDataException>(
            () => RecoverySecretEnvelope.Unwrap(envelope, RepositoryId, RecoveryEntropy));
    }

    [Fact]
    public void ModifyingTheCiphertextBreaksUnwrapping()
    {
        var envelope = WrapSecret();
        var wrapped = envelope.WrappedSecret.ToArray();
        wrapped[^1] ^= 0x01;

        Assert.Throws<UnlockFailedException>(
            () => RecoverySecretEnvelope.Unwrap(envelope with { WrappedSecret = wrapped }, RepositoryId, RecoveryEntropy));
    }

    [Fact]
    public void TrailingDataIsRejected()
    {
        var encoded = KeyEnvelopeCodec.Encode(WrapSecret());

        byte[] withTrailingByte = [.. encoded, 0x00];

        Assert.Throws<InvalidDataException>(() => KeyEnvelopeCodec.Decode(withTrailingByte));
    }

    [Fact]
    public void AnUnknownCriticalFieldIsRejected()
    {
        var envelope = WrapSecret() with { Critical = ["envelopeId", "quorum"] };
        var encoded = KeyEnvelopeCodec.Encode(envelope);

        Assert.Throws<InvalidDataException>(() => KeyEnvelopeCodec.Decode(encoded));
    }

    [Fact]
    public void AnIndefiniteLengthEncodingIsRejected()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);
        writer.WriteTextString("schema");
        writer.WriteTextString(KeyEnvelopeV1.Schema);
        writer.WriteEndMap();

        Assert.Throws<InvalidDataException>(() => KeyEnvelopeCodec.Decode(writer.Encode()));
    }

    [Fact]
    public void GarbageIsRejectedWithoutAnUnboundedAllocation()
    {
        Assert.Throws<InvalidDataException>(() => KeyEnvelopeCodec.Decode(RandomNumberGenerator.GetBytes(256)));
        Assert.Throws<InvalidDataException>(() => KeyEnvelopeCodec.Decode(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void TwoEnvelopesOverTheSameSecretUseDifferentKeys()
    {
        var first = KeyEnvelopeCodec.Encode(WrapSecret());
        var second = KeyEnvelopeCodec.Encode(WrapSecret());

        Assert.NotEqual(first, second);
    }

    private static KeyEnvelopeV1 WrapSecret()
    {
        using var lease = new BufferKeyLease(EngineUnlockSecret);
        return RecoverySecretEnvelope.Wrap(RepositoryId, RecoveryEntropy, lease);
    }
}
