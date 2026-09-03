using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Bip39RecoveryEnvelopeV1 of ADR-002: the recovery mnemonic produces a standard BIP-39 seed, and a
/// separate Fortiq-context HKDF derives the key that wraps the Engine Unlock Secret.
/// </summary>
/// <remarks>
/// Every syntactically valid mnemonic and passphrase produces some seed, so only a successful AEAD
/// authentication proves the recovery material was right. An optional passphrase is a second factor
/// and is never stored in the recovery kit.
/// </remarks>
public static class Bip39RecoveryEnvelope
{
    public const string SuiteId = "bip39-pbkdf2-hmac-sha512-hkdf-sha256-aes256gcm-v1";

    public static KeyEnvelopeV1 Wrap(
        ReadOnlySpan<byte> repositoryId,
        string mnemonic,
        IKeyLease engineUnlockSecret,
        string? passphrase = null,
        TimeProvider? clock = null)
    {
        // Decoding first rejects a mnemonic with a wrong word or checksum, so a kit is never created
        // around recovery material the user cannot type back correctly.
        var entropy = Bip39Mnemonic.Decode(mnemonic);
        CryptographicOperations.ZeroMemory(entropy);

        var seed = Bip39Mnemonic.DeriveSeed(mnemonic, passphrase);
        try
        {
            return EnvelopeCipher.Wrap(
                SuiteId,
                EnvelopeProviderType.Bip39,
                seed,
                repositoryId,
                engineUnlockSecret,
                providerParameters: null,
                clock);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Opens an envelope with a recovery mnemonic. A malformed mnemonic is reported as a format
    /// error before any derivation; a well-formed but wrong one fails as <see cref="UnlockFailedException"/>,
    /// like every other unlock failure.
    /// </summary>
    public static IKeyLease Unwrap(
        KeyEnvelopeV1 envelope,
        ReadOnlySpan<byte> repositoryId,
        string mnemonic,
        string? passphrase = null)
    {
        var entropy = Bip39Mnemonic.Decode(mnemonic);
        CryptographicOperations.ZeroMemory(entropy);

        var seed = Bip39Mnemonic.DeriveSeed(mnemonic, passphrase);
        try
        {
            return EnvelopeCipher.Unwrap(envelope, SuiteId, EnvelopeProviderType.Bip39, seed, repositoryId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}
