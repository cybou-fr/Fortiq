using System.Security.Cryptography;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

/// <summary>
/// A suite identifier decides how an envelope is opened; the provider type beside it is what policy
/// reads. The two may never disagree, or policy would be deciding about a different envelope than
/// the one the cryptography would open.
/// </summary>
public sealed class EnvelopeSuiteTests
{
    private static readonly byte[] RepositoryId = [.. Enumerable.Range(0, 32).Select(index => (byte)index)];

    [Theory]
    [InlineData(EnvelopeProviderType.WindowsTpm)]
    [InlineData(EnvelopeProviderType.Password)]
    [InlineData(EnvelopeProviderType.EnterpriseKms)]
    public void AMnemonicSuiteCannotClaimAnotherProvider(EnvelopeProviderType providerType)
    {
        using var lease = new BufferKeyLease(RandomNumberGenerator.GetBytes(32));

        Assert.Throws<InvalidDataException>(
            () => EnvelopeCipher.Wrap(
                Bip39RecoveryEnvelope.SuiteId,
                providerType,
                RandomNumberGenerator.GetBytes(32),
                RepositoryId,
                lease,
                providerParameters: null,
                clock: null));
    }

    [Fact]
    public void ADeviceSuiteCannotClaimToBeARecoveryMethod()
    {
        using var lease = new BufferKeyLease(RandomNumberGenerator.GetBytes(32));

        Assert.Throws<InvalidDataException>(
            () => EnvelopeCipher.Wrap(
                WindowsTpmEnvelope.SuiteId,
                EnvelopeProviderType.Bip39,
                RandomNumberGenerator.GetBytes(32),
                RepositoryId,
                lease,
                providerParameters: null,
                clock: null));
    }

    [Fact]
    public void ADecodedEnvelopeWhoseSuiteContradictsItsProviderIsRefused()
    {
        using var lease = new BufferKeyLease(RandomNumberGenerator.GetBytes(32));
        var device = EnvelopeCipher.Wrap(
            WindowsTpmEnvelope.SuiteId,
            EnvelopeProviderType.WindowsTpm,
            RandomNumberGenerator.GetBytes(32),
            RepositoryId,
            lease,
            providerParameters: null,
            clock: null);

        // Relabelled after the fact: the file claims a recovery provider for a device suite, which
        // is exactly what would slip a device-only kit past the policy that keeps a way back.
        var relabelled = KeyEnvelopeCodec.Encode(device with { ProviderType = EnvelopeProviderType.Bip39 });

        Assert.Throws<InvalidDataException>(() => KeyEnvelopeCodec.Decode(relabelled));
    }

    [Fact]
    public void KnownSuitesMapToTheProviderThatOpensThem()
    {
        Assert.Equal(EnvelopeProviderType.Bip39, EnvelopeSuites.ProviderTypeFor(Bip39RecoveryEnvelope.SuiteId));
        Assert.Equal(EnvelopeProviderType.Bip39, EnvelopeSuites.ProviderTypeFor(RecoverySecretEnvelope.SuiteId));
        Assert.Equal(EnvelopeProviderType.WindowsTpm, EnvelopeSuites.ProviderTypeFor(WindowsTpmEnvelope.SuiteId));

        // An unknown suite is not judged here: the provider asked to open it refuses it instead.
        Assert.Null(EnvelopeSuites.ProviderTypeFor("something-newer-v2"));
    }
}
