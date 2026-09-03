using System.Security.Cryptography;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

/// <summary>
/// BIP-39 checked against the reference vectors published with the specification, plus the Fortiq
/// envelope built on top of them.
/// </summary>
public sealed class Bip39Tests
{
    private const string VectorPassphrase = "TREZOR";
    private const string WordlistSha256 = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda";

    private static readonly byte[] RepositoryId = [.. Enumerable.Range(0, 32).Select(index => (byte)index)];
    private static readonly byte[] EngineUnlockSecret = [.. Enumerable.Range(0, 32).Select(index => (byte)(index * 5))];

    public static TheoryData<string, string, string> OfficialVectors()
    {
        var data = new TheoryData<string, string, string>();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bip39-vectors.json")));
        foreach (var vector in document.RootElement.GetProperty("english").EnumerateArray())
        {
            data.Add(vector[0].GetString()!, vector[1].GetString()!, vector[2].GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OfficialVectors))]
    public void MatchesTheOfficialVectors(string entropyHex, string mnemonic, string seedHex)
    {
        var entropy = Convert.FromHexString(entropyHex);

        Assert.Equal(mnemonic, Bip39Mnemonic.Encode(entropy));
        Assert.Equal(entropy, Bip39Mnemonic.Decode(mnemonic));
        Assert.Equal(seedHex, Convert.ToHexStringLower(Bip39Mnemonic.DeriveSeed(mnemonic, VectorPassphrase)));
    }

    [Fact]
    public void TheEmbeddedWordlistIsTheOneItsProvenanceRecords()
    {
        var normalized = System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', Bip39Mnemonic.Wordlist) + '\n');

        Assert.Equal(2048, Bip39Mnemonic.Wordlist.Count);
        Assert.Equal(WordlistSha256, Convert.ToHexStringLower(SHA256.HashData(normalized)));
        Assert.Equal(Bip39Mnemonic.Wordlist.Order(StringComparer.Ordinal), Bip39Mnemonic.Wordlist);
        // BIP-39 requires the first four letters to identify a word; the shortest words are three
        // letters long and are their own prefix.
        Assert.Equal(
            2048,
            Bip39Mnemonic.Wordlist.Select(word => word[..Math.Min(4, word.Length)]).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("abandon abandon abandon")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon fortiq")]
    public void AMalformedMnemonicIsRejectedBeforeAnyDerivation(string mnemonic) =>
        Assert.Throws<FormatException>(() => Bip39Mnemonic.Decode(mnemonic));

    [Fact]
    public void OddSpacingAndCaseStillResolveToTheSameEntropy()
    {
        var mnemonic = Bip39Mnemonic.Create();
        var mangled = "  " + mnemonic.ToUpperInvariant().Replace(" ", "   ", StringComparison.Ordinal) + "\n";

        Assert.Equal(Bip39Mnemonic.Decode(mnemonic), Bip39Mnemonic.Decode(mangled));
    }

    [Fact]
    public void ACreatedMnemonicRoundTripsThroughAnEnvelope()
    {
        var mnemonic = Bip39Mnemonic.Create();
        var envelope = KeyEnvelopeCodec.Decode(KeyEnvelopeCodec.Encode(Wrap(mnemonic)));

        using var lease = Bip39RecoveryEnvelope.Unwrap(envelope, RepositoryId, mnemonic);
        var recovered = new byte[lease.Length];
        lease.CopyTo(recovered);

        Assert.Equal(EngineUnlockSecret, recovered);
        Assert.Equal(Bip39RecoveryEnvelope.SuiteId, envelope.Suite);
    }

    [Fact]
    public void ADifferentMnemonicFailsAsUnlockFailed()
    {
        var envelope = Wrap(Bip39Mnemonic.Create());

        Assert.Throws<UnlockFailedException>(
            () => Bip39RecoveryEnvelope.Unwrap(envelope, RepositoryId, Bip39Mnemonic.Create()));
    }

    [Fact]
    public void APassphraseIsRequiredToOpenAnEnvelopeCreatedWithOne()
    {
        var mnemonic = Bip39Mnemonic.Create();
        var envelope = Bip39RecoveryEnvelope.Wrap(RepositoryId, mnemonic, Lease(), "second factor");

        Assert.Throws<UnlockFailedException>(() => Bip39RecoveryEnvelope.Unwrap(envelope, RepositoryId, mnemonic));

        using var lease = Bip39RecoveryEnvelope.Unwrap(envelope, RepositoryId, mnemonic, "second factor");
        Assert.Equal(EngineUnlockSecret.Length, lease.Length);
    }

    [Fact]
    public void TheEntropySuiteAndTheMnemonicSuiteDoNotOpenEachOther()
    {
        var mnemonic = Bip39Mnemonic.Create();
        var entropy = Bip39Mnemonic.Decode(mnemonic);
        var mnemonicEnvelope = Wrap(mnemonic);

        // Both suites use the bip39 provider type, so only the suite identifier separates them, and
        // it must be refused rather than silently reinterpreted.
        Assert.Throws<InvalidDataException>(
            () => RecoverySecretEnvelope.Unwrap(mnemonicEnvelope, RepositoryId, entropy));
    }

    private static KeyEnvelopeV1 Wrap(string mnemonic) =>
        Bip39RecoveryEnvelope.Wrap(RepositoryId, mnemonic, Lease());

    private static BufferKeyLease Lease() => new(EngineUnlockSecret);
}
