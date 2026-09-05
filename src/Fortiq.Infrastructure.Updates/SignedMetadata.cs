using System.Text.Json;

namespace Fortiq.Infrastructure.Updates;

/// <summary>One signature over a role document, and the key that is claimed to have made it.</summary>
public readonly record struct TufSignature(string KeyId, byte[] Signature);

/// <summary>
/// A parsed <c>{ "signatures": [...], "signed": {...} }</c> envelope, holding the exact bytes the
/// signatures cover.
/// </summary>
/// <remarks>
/// The canonical bytes are computed once, when the envelope is read, and every later verification uses
/// that same array. Re-canonicalising per signature would open the door to the document being read
/// twice and differing between reads; holding the bytes means every key in a threshold is answering a
/// question about one specific sequence of bytes.
/// </remarks>
public sealed class SignedMetadata
{
    private SignedMetadata(
        JsonElement signed,
        IReadOnlyList<TufSignature> signatures,
        byte[] canonicalSigned)
    {
        Payload = signed;
        Signatures = signatures;
        CanonicalSigned = canonicalSigned;
    }

    /// <summary>The role document itself - the part the signatures cover.</summary>
    public JsonElement Payload { get; }

    public IReadOnlyList<TufSignature> Signatures { get; }

    /// <summary>The bytes the signatures are over.</summary>
    public byte[] CanonicalSigned { get; }

    /// <summary>The <c>_type</c> the document declares, used to refuse a document served as the wrong role.</summary>
    public string Type => Payload.TryGetProperty("_type", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()!
        : throw new TufMetadataException("A role document has no '_type' string.");

    /// <summary>The document's version, which never decreases for a given role.</summary>
    public long Version => Payload.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.Number
        ? value.GetInt64()
        : throw new TufMetadataException("A role document has no integer 'version'.");

    /// <summary>The instant after which the document is stale and must not be trusted.</summary>
    public DateTimeOffset Expires
    {
        get
        {
            if (!Payload.TryGetProperty("expires", out var value) || value.ValueKind != JsonValueKind.String)
            {
                throw new TufMetadataException("A role document has no 'expires' string.");
            }

            if (!DateTimeOffset.TryParse(
                    value.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var expires))
            {
                throw new TufMetadataException($"A role document's 'expires' value '{value.GetString()}' is not a date.");
            }

            return expires;
        }
    }

    public static SignedMetadata Parse(ReadOnlySpan<byte> utf8)
    {
        // Parsed and immediately released: the elements kept are clones, which outlive the document
        // they came from. Holding the document instead would make every metadata object disposable and
        // every path that forgets to dispose one a slow leak in a service that runs for months.
        using var document = ParseDocument(utf8);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException("Update metadata is not a JSON object.");
        }

        if (!root.TryGetProperty("signed", out var signed) || signed.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException("Update metadata has no 'signed' object.");
        }

        if (!root.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array)
        {
            throw new TufMetadataException("Update metadata has no 'signatures' array.");
        }

        var parsed = new List<TufSignature>();
        foreach (var signature in signatures.EnumerateArray())
        {
            parsed.Add(ReadSignature(signature));
        }

        return new SignedMetadata(signed.Clone(), parsed, CanonicalJson.Encode(signed));
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException error)
        {
            throw new TufMetadataException("Update metadata is not valid JSON.", error);
        }
    }

    private static TufSignature ReadSignature(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("keyid", out var keyId) || keyId.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("sig", out var signature) || signature.ValueKind != JsonValueKind.String)
        {
            throw new TufMetadataException("A signature entry has no 'keyid' and 'sig' strings.");
        }

        try
        {
            return new TufSignature(keyId.GetString()!, Convert.FromHexString(signature.GetString()!));
        }
        catch (FormatException error)
        {
            throw new TufMetadataException("A signature is not hexadecimal.", error);
        }
    }
}
