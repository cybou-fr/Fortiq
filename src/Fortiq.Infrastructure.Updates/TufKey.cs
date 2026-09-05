using System.Security.Cryptography;
using System.Text.Json;

namespace Fortiq.Infrastructure.Updates;

/// <summary>
/// A public key a TUF role may sign with, and the identifier the role documents refer to it by.
/// </summary>
/// <remarks>
/// Only <c>ecdsa-sha2-nistp256</c> is accepted. TUF's more common choice is Ed25519, which .NET does
/// not implement: <c>System.Security.Cryptography</c> has no Ed25519 type in .NET 10, so supporting it
/// would mean taking a third-party cryptographic dependency into the path that decides whether a
/// binary may replace a running backup service. ADR-013 exists because that decision is expensive, and
/// P-256 is a scheme the specification already blesses and the platform already implements. The
/// scheme is named in every key object, so adding Ed25519 later is an addition rather than a break.
/// </remarks>
public sealed class TufKey
{
    /// <summary>The only signature scheme this implementation accepts.</summary>
    public const string EcdsaP256Scheme = "ecdsa-sha2-nistp256";

    /// <summary>The only key type this implementation accepts.</summary>
    public const string EcdsaKeyType = "ecdsa";

    private readonly byte[] _subjectPublicKeyInfo;

    private TufKey(string keyId, string keyType, string scheme, byte[] subjectPublicKeyInfo)
    {
        KeyId = keyId;
        KeyType = keyType;
        Scheme = scheme;
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
    }

    /// <summary>The identifier role documents use to name this key.</summary>
    public string KeyId { get; }

    public string KeyType { get; }

    public string Scheme { get; }

    /// <summary>
    /// Reads a key object and derives its identifier from its own content.
    /// </summary>
    /// <param name="element">A TUF key object: <c>keytype</c>, <c>scheme</c> and <c>keyval.public</c>.</param>
    /// <remarks>
    /// The identifier is not read from the document. TUF computes it as the SHA-256 of the key
    /// object's canonical form, so a key that claims an identifier belonging to a different key is
    /// simply not that key - there is no name to trust separately from the material it names.
    /// </remarks>
    public static TufKey Read(JsonElement element)
    {
        var keyType = RequiredString(element, "keytype");
        var scheme = RequiredString(element, "scheme");

        if (!string.Equals(keyType, EcdsaKeyType, StringComparison.Ordinal) ||
            !string.Equals(scheme, EcdsaP256Scheme, StringComparison.Ordinal))
        {
            throw new TufMetadataException(
                $"Unsupported key type '{keyType}' with scheme '{scheme}'. " +
                $"Only '{EcdsaKeyType}' keys using '{EcdsaP256Scheme}' are accepted.");
        }

        if (!element.TryGetProperty("keyval", out var keyValue) ||
            keyValue.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException("A key object has no 'keyval' object.");
        }

        var encoded = RequiredString(keyValue, "public");
        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromHexString(encoded);
        }
        catch (FormatException error)
        {
            throw new TufMetadataException("A key's public material is not hexadecimal.", error);
        }

        // Importing here rather than at first use: a key that cannot be parsed is a malformed root
        // document, and that should be refused while reading it, not when a signature happens to be
        // checked against it much later.
        using (var probe = ECDsa.Create())
        {
            try
            {
                probe.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
                if (read != subjectPublicKeyInfo.Length)
                {
                    throw new TufMetadataException("A key's public material has trailing bytes.");
                }

                if (probe.KeySize != 256)
                {
                    throw new TufMetadataException(
                        $"A key declares scheme '{EcdsaP256Scheme}' but carries a {probe.KeySize}-bit key.");
                }
            }
            catch (CryptographicException error)
            {
                throw new TufMetadataException("A key's public material is not a valid SubjectPublicKeyInfo.", error);
            }
        }

        return new TufKey(ComputeKeyId(element), keyType, scheme, subjectPublicKeyInfo);
    }

    /// <summary>True when <paramref name="signature"/> is this key's signature over <paramref name="payload"/>.</summary>
    public bool Verifies(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);

        try
        {
            return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            // A malformed signature is a failed verification, not an error to propagate. An attacker
            // who can make verification throw where a wrong signature would return false has turned a
            // rejection into a crash, and a crashing updater is one somebody switches off.
            return false;
        }
    }

    private static string ComputeKeyId(JsonElement element)
    {
        var canonical = CanonicalJson.Encode(element);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new TufMetadataException($"A key object has no '{name}' string.");
        }

        return value.GetString()!;
    }
}
