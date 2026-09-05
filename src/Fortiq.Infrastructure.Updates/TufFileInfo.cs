using System.Security.Cryptography;
using System.Text.Json;

namespace Fortiq.Infrastructure.Updates;

/// <summary>What a trusted document says a file must be: its exact length and SHA-256.</summary>
public sealed record TufFileInfo(long Length, string Sha256)
{
    /// <summary>Reads a <c>{ "length": n, "hashes": { "sha256": "..." } }</c> object.</summary>
    public static TufFileInfo Read(JsonElement element, string describedFile)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException($"The entry for '{describedFile}' is not an object.");
        }

        if (!element.TryGetProperty("length", out var lengthValue) ||
            lengthValue.ValueKind != JsonValueKind.Number ||
            !lengthValue.TryGetInt64(out var length) ||
            length < 0)
        {
            throw new TufMetadataException($"The entry for '{describedFile}' has no non-negative integer 'length'.");
        }

        if (!element.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException($"The entry for '{describedFile}' has no 'hashes' object.");
        }

        // SHA-256 specifically, not "whichever hash the document happens to offer". Letting the
        // document choose lets it choose a weak one, and an attacker who can pick the algorithm has
        // already picked the one they can forge.
        if (!hashes.TryGetProperty("sha256", out var sha256) || sha256.ValueKind != JsonValueKind.String)
        {
            throw new TufMetadataException($"The entry for '{describedFile}' has no 'sha256' hash.");
        }

        var digest = sha256.GetString()!;
        if (digest.Length != 64 || !IsHex(digest))
        {
            throw new TufMetadataException($"The 'sha256' hash for '{describedFile}' is not 64 hexadecimal characters.");
        }

        return new TufFileInfo(length, digest.ToLowerInvariant());
    }

    /// <summary>
    /// Refuses <paramref name="content"/> unless it is exactly the file this entry describes.
    /// </summary>
    /// <remarks>
    /// Length is checked before the hash, and not only as an optimisation: it bounds how much of an
    /// attacker-supplied stream is read at all. Hashing first would mean digesting whatever was sent,
    /// however large, before discovering it was the wrong file.
    /// </remarks>
    public void RequireMatch(ReadOnlySpan<byte> content, string describedFile)
    {
        if (content.Length != Length)
        {
            throw new TufMetadataException(
                $"'{describedFile}' is {content.Length} byte(s); the trusted metadata says it is {Length}.");
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(content, digest);
        var actual = Convert.ToHexStringLower(digest);

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(Sha256)))
        {
            throw new TufMetadataException(
                $"'{describedFile}' hashes to {actual}; the trusted metadata says {Sha256}.");
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
