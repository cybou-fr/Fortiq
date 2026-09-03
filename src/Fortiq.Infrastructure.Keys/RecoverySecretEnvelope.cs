using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Wraps the Engine Unlock Secret directly under 256 bits of recovery entropy. It is the suite used
/// when the entropy is handled as raw bytes rather than as a mnemonic; <see cref="Bip39RecoveryEnvelope"/>
/// covers the human-readable path of ADR-002.
/// </summary>
/// <remarks>
/// It uses only HKDF-SHA-256 and AES-256-GCM from the platform, so it carries no password KDF and is
/// unaffected by the Argon2 dependency gate of ADR-013.
/// </remarks>
public static class RecoverySecretEnvelope
{
    public const string SuiteId = "recovery-entropy-hkdf-sha256-aes256gcm-v1";
    public const int RecoveryEntropySize = 32;

    public static KeyEnvelopeV1 Wrap(
        ReadOnlySpan<byte> repositoryId,
        ReadOnlySpan<byte> recoveryEntropy,
        IKeyLease engineUnlockSecret,
        TimeProvider? clock = null)
    {
        RequireEntropy(recoveryEntropy);
        return EnvelopeCipher.Wrap(
            SuiteId,
            EnvelopeProviderType.Bip39,
            recoveryEntropy,
            repositoryId,
            engineUnlockSecret,
            providerParameters: null,
            clock);
    }

    /// <summary>
    /// Opens an envelope. Every failure - wrong entropy, a modified field, a repository mismatch or
    /// a failed authentication tag - surfaces as the same <see cref="UnlockFailedException"/>, so an
    /// attacker learns nothing from which attempt failed.
    /// </summary>
    public static IKeyLease Unwrap(
        KeyEnvelopeV1 envelope,
        ReadOnlySpan<byte> repositoryId,
        ReadOnlySpan<byte> recoveryEntropy)
    {
        RequireEntropy(recoveryEntropy);
        return EnvelopeCipher.Unwrap(envelope, SuiteId, EnvelopeProviderType.Bip39, recoveryEntropy, repositoryId);
    }

    private static void RequireEntropy(ReadOnlySpan<byte> recoveryEntropy)
    {
        if (recoveryEntropy.Length != RecoveryEntropySize)
        {
            throw new ArgumentException("Recovery entropy must contain exactly 32 bytes.", nameof(recoveryEntropy));
        }
    }
}
