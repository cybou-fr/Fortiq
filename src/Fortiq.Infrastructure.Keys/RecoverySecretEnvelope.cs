using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Wraps the Engine Unlock Secret under 256 bits of recovery entropy, which BIP-39 later encodes as
/// a mnemonic. It uses only HKDF-SHA-256 and AES-256-GCM from the platform, so it carries no
/// password KDF and is unaffected by the Argon2 dependency gate of ADR-013.
/// </summary>
/// <remarks>
/// The suite identifier is versioned. A recovery tool that meets an unknown suite must fail with a
/// clear error rather than guess parameters.
/// </remarks>
public static class RecoverySecretEnvelope
{
    public const string SuiteId = "recovery-entropy-hkdf-sha256-aes256gcm-v1";
    public const int RecoveryEntropySize = 32;
    private const int KeyEncryptionKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly string[] CriticalFields =
        ["envelopeId", "repositoryId", "engineId", "purpose", "providerType", "suite", "version"];

    public static KeyEnvelopeV1 Wrap(
        ReadOnlySpan<byte> repositoryId,
        ReadOnlySpan<byte> recoveryEntropy,
        IKeyLease engineUnlockSecret,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(engineUnlockSecret);
        Validate(repositoryId, recoveryEntropy);

        var secret = new byte[engineUnlockSecret.Length];
        var wrapped = new byte[NonceSize + secret.Length + TagSize];
        Span<byte> key = stackalloc byte[KeyEncryptionKeySize];
        try
        {
            engineUnlockSecret.CopyTo(secret);
            var nonce = wrapped.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            var envelope = new KeyEnvelopeV1
            {
                EnvelopeId = RandomNumberGenerator.GetBytes(KeyEnvelopeV1.EnvelopeIdSize),
                RepositoryId = repositoryId.ToArray(),
                ProviderType = EnvelopeProviderType.Bip39,
                Suite = SuiteId,
                ProviderParameters = new Dictionary<string, byte[]>(StringComparer.Ordinal),
                WrappedSecret = wrapped,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()),
                Critical = CriticalFields
            };

            DeriveKeyEncryptionKey(recoveryEntropy, envelope, key);
            using var aead = new AesGcm(key, TagSize);
            aead.Encrypt(
                nonce,
                secret,
                wrapped.AsSpan(NonceSize, secret.Length),
                wrapped.AsSpan(NonceSize + secret.Length, TagSize),
                KeyEnvelopeCodec.AuthenticatedContext(envelope));

            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(key);
        }
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
        ArgumentNullException.ThrowIfNull(envelope);
        Validate(repositoryId, recoveryEntropy);

        if (envelope.Suite != SuiteId)
        {
            throw new InvalidDataException("Unsupported envelope suite; this tool cannot open it.");
        }

        if (envelope.ProviderType != EnvelopeProviderType.Bip39
            || envelope.Purpose != KeyEnvelopeV1.EngineUnlockPurpose
            || !CryptographicOperations.FixedTimeEquals(envelope.RepositoryId, repositoryId)
            || envelope.WrappedSecret.Length <= NonceSize + TagSize)
        {
            throw new UnlockFailedException();
        }

        var secretLength = envelope.WrappedSecret.Length - NonceSize - TagSize;
        var secret = new byte[secretLength];
        Span<byte> key = stackalloc byte[KeyEncryptionKeySize];
        try
        {
            DeriveKeyEncryptionKey(recoveryEntropy, envelope, key);
            using var aead = new AesGcm(key, TagSize);
            aead.Decrypt(
                envelope.WrappedSecret.AsSpan(0, NonceSize),
                envelope.WrappedSecret.AsSpan(NonceSize, secretLength),
                envelope.WrappedSecret.AsSpan(NonceSize + secretLength, TagSize),
                secret,
                KeyEnvelopeCodec.AuthenticatedContext(envelope));

            return new BufferKeyLease(secret);
        }
        catch (CryptographicException)
        {
            throw new UnlockFailedException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void DeriveKeyEncryptionKey(ReadOnlySpan<byte> recoveryEntropy, KeyEnvelopeV1 envelope, Span<byte> key)
    {
        // The salt is the envelope ID and the info is the ADR-002 derivation context, so two
        // envelopes over the same recovery entropy never derive the same key.
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            recoveryEntropy,
            key,
            envelope.EnvelopeId,
            envelope.DerivationContext());
    }

    private static void Validate(ReadOnlySpan<byte> repositoryId, ReadOnlySpan<byte> recoveryEntropy)
    {
        if (repositoryId.Length != KeyEnvelopeV1.RepositoryIdSize)
        {
            throw new ArgumentException("Repository ID must contain exactly 32 bytes.", nameof(repositoryId));
        }

        if (recoveryEntropy.Length != RecoveryEntropySize)
        {
            throw new ArgumentException("Recovery entropy must contain exactly 32 bytes.", nameof(recoveryEntropy));
        }
    }
}
