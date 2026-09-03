using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// The wrap and unwrap step shared by every envelope suite: HKDF-SHA-256 over the provider's input
/// key material, then AES-256-GCM over the Engine Unlock Secret with the envelope as authenticated
/// data. Each provider decides how it obtains its input key material.
/// </summary>
internal static class EnvelopeCipher
{
    internal const int KeyEncryptionKeySize = 32;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;

    internal static readonly string[] CriticalFields =
        ["envelopeId", "repositoryId", "engineId", "purpose", "providerType", "suite", "version"];

    internal static KeyEnvelopeV1 Wrap(
        string suite,
        EnvelopeProviderType providerType,
        ReadOnlySpan<byte> inputKeyMaterial,
        ReadOnlySpan<byte> repositoryId,
        IKeyLease engineUnlockSecret,
        IReadOnlyDictionary<string, byte[]>? providerParameters,
        TimeProvider? clock)
    {
        ArgumentNullException.ThrowIfNull(engineUnlockSecret);
        if (repositoryId.Length != KeyEnvelopeV1.RepositoryIdSize)
        {
            throw new ArgumentException("Repository ID must contain exactly 32 bytes.", nameof(repositoryId));
        }

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
                ProviderType = providerType,
                Suite = suite,
                ProviderParameters = providerParameters ?? new Dictionary<string, byte[]>(StringComparer.Ordinal),
                WrappedSecret = wrapped,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()),
                Critical = CriticalFields
            };

            DeriveKeyEncryptionKey(inputKeyMaterial, envelope, key);
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

    internal static IKeyLease Unwrap(
        KeyEnvelopeV1 envelope,
        string suite,
        EnvelopeProviderType providerType,
        ReadOnlySpan<byte> inputKeyMaterial,
        ReadOnlySpan<byte> repositoryId)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Suite != suite)
        {
            throw new InvalidDataException("Unsupported envelope suite; this tool cannot open it.");
        }

        if (envelope.ProviderType != providerType
            || envelope.Purpose != KeyEnvelopeV1.EngineUnlockPurpose
            || repositoryId.Length != KeyEnvelopeV1.RepositoryIdSize
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
            DeriveKeyEncryptionKey(inputKeyMaterial, envelope, key);
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

    private static void DeriveKeyEncryptionKey(ReadOnlySpan<byte> inputKeyMaterial, KeyEnvelopeV1 envelope, Span<byte> key)
    {
        // The salt is the envelope ID and the info is the ADR-002 derivation context, so two
        // envelopes over the same input key material never derive the same key.
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            inputKeyMaterial,
            key,
            envelope.EnvelopeId,
            envelope.DerivationContext());
    }
}
