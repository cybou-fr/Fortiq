using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fortiq.ControlPlane;

/// <summary>
/// A cryptographically signed envelope enclosing a control plane document (telemetry, policy, enrollment).
/// The signature is computed strictly over the OLPC Canonical JSON encoding of the payload.
/// </summary>
public sealed record SignedFleetEnvelope(
    string PayloadJson,
    string KeyId,
    string SignatureHex,
    string Schema = SignedFleetEnvelope.EnvelopeSchema,
    int Version = SignedFleetEnvelope.EnvelopeVersion)
{
    public const string EnvelopeSchema = "fortiq.fleet-envelope";
    public const int EnvelopeVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static SignedFleetEnvelope Sign<T>(T payload, DeviceKey key)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(key);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var canonicalBytes = CanonicalJson.Encode(json);
        var signatureHex = key.SignHex(canonicalBytes);

        return new SignedFleetEnvelope(json, key.KeyId, signatureHex);
    }

    public bool Verify(string publicKeyHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyHex);

        var expectedKeyId = DeviceKey.ComputeKeyIdFromPublicKey(publicKeyHex);
        if (!string.Equals(expectedKeyId, KeyId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var canonicalBytes = CanonicalJson.Encode(PayloadJson);
            return DeviceKey.Verify(publicKeyHex, canonicalBytes, SignatureHex);
        }
        catch
        {
            return false;
        }
    }

    public T Unpack<T>()
    {
        return JsonSerializer.Deserialize<T>(PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize payload JSON.");
    }
}
