using System.Formats.Cbor;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Deterministic CBOR (RFC 8949) encoding of <see cref="KeyEnvelopeV1"/>. The decoder is strict by
/// design: it accepts only the defined types and sizes, rejects duplicate keys, indefinite-length
/// values, trailing data and any critical field it does not understand.
/// </summary>
public static class KeyEnvelopeCodec
{
    private const string SchemaKey = "schema";
    private const string VersionKey = "version";
    private const string EnvelopeIdKey = "envelopeId";
    private const string RepositoryIdKey = "repositoryId";
    private const string EngineIdKey = "engineId";
    private const string PurposeKey = "purpose";
    private const string ProviderTypeKey = "providerType";
    private const string SuiteKey = "suite";
    private const string ProviderParametersKey = "providerParameters";
    private const string WrappedSecretKey = "wrappedSecret";
    private const string CreatedAtKey = "createdAt";
    private const string CriticalKey = "critical";

    private const int MaximumSuiteLength = 64;
    private const int MaximumParameters = 16;
    private const int MaximumParameterSize = 1024;
    private const int MaximumWrappedSecretSize = 8 * 1024;

    private static readonly HashSet<string> UnderstoodCriticalFields = new(StringComparer.Ordinal)
    {
        EnvelopeIdKey, RepositoryIdKey, EngineIdKey, PurposeKey, ProviderTypeKey, SuiteKey, VersionKey
    };

    public static byte[] Encode(KeyEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var writer = new CborWriter(CborConformanceMode.Canonical, convertIndefiniteLengthEncodings: false);
        WriteFields(writer, envelope, includeWrappedSecret: true);
        var encoded = writer.Encode();
        return encoded.Length <= KeyEnvelopeV1.MaximumEncodedSize
            ? encoded
            : throw new InvalidDataException("Encoded envelope exceeds the maximum size.");
    }

    /// <summary>
    /// The authenticated context passed to the AEAD as associated data: every public field except
    /// the wrapped secret itself, in the same deterministic encoding.
    /// </summary>
    public static byte[] AuthenticatedContext(KeyEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var writer = new CborWriter(CborConformanceMode.Canonical, convertIndefiniteLengthEncodings: false);
        WriteFields(writer, envelope, includeWrappedSecret: false);
        return writer.Encode();
    }

    public static KeyEnvelopeV1 Decode(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length is 0 or > KeyEnvelopeV1.MaximumEncodedSize)
        {
            throw new InvalidDataException("Envelope is empty or exceeds the maximum size.");
        }

        try
        {
            return DecodeCore(encoded);
        }
        catch (CborContentException error)
        {
            throw new InvalidDataException("Envelope is not valid deterministic CBOR.", error);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("Envelope contains an unexpected CBOR type.", error);
        }
    }

    private static KeyEnvelopeV1 DecodeCore(ReadOnlyMemory<byte> encoded)
    {
        var reader = new CborReader(encoded, CborConformanceMode.Canonical);
        var count = reader.ReadStartMap() ?? throw new InvalidDataException("Indefinite-length maps are rejected.");

        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadTextString();
            object value = key switch
            {
                SchemaKey or EngineIdKey or PurposeKey or ProviderTypeKey or SuiteKey => reader.ReadTextString(),
                VersionKey => reader.ReadInt32(),
                CreatedAtKey => reader.ReadInt64(),
                EnvelopeIdKey or RepositoryIdKey or WrappedSecretKey => reader.ReadByteString(),
                ProviderParametersKey => ReadParameters(reader),
                CriticalKey => ReadCritical(reader),
                _ => throw new InvalidDataException("Envelope contains an unknown field.")
            };

            if (!fields.TryAdd(key, value))
            {
                throw new InvalidDataException("Envelope contains a duplicate field.");
            }
        }

        reader.ReadEndMap();
        if (reader.BytesRemaining != 0)
        {
            throw new InvalidDataException("Envelope has trailing data.");
        }

        if (Text(fields, SchemaKey) != KeyEnvelopeV1.Schema
            || (int)Require(fields, VersionKey) != KeyEnvelopeV1.SchemaVersion)
        {
            throw new InvalidDataException("Unsupported envelope schema or version.");
        }

        if (Text(fields, EngineIdKey) != KeyEnvelopeV1.EngineId)
        {
            throw new InvalidDataException("Unsupported engine identifier.");
        }

        var critical = (IReadOnlyList<string>)Require(fields, CriticalKey);
        foreach (var field in critical)
        {
            if (!UnderstoodCriticalFields.Contains(field))
            {
                throw new InvalidDataException("Envelope requires a critical field this version does not understand.");
            }
        }

        var suite = Text(fields, SuiteKey);
        if (suite.Length is 0 or > MaximumSuiteLength)
        {
            throw new InvalidDataException("Envelope suite identifier has an invalid length.");
        }

        var providerType = KeyEnvelopeV1.ParseProvider(Text(fields, ProviderTypeKey));
        EnvelopeSuites.RequireConsistent(suite, providerType);

        return new KeyEnvelopeV1
        {
            EnvelopeId = Bytes(fields, EnvelopeIdKey, KeyEnvelopeV1.EnvelopeIdSize, KeyEnvelopeV1.EnvelopeIdSize),
            RepositoryId = Bytes(fields, RepositoryIdKey, KeyEnvelopeV1.RepositoryIdSize, KeyEnvelopeV1.RepositoryIdSize),
            ProviderType = providerType,
            Suite = suite,
            ProviderParameters = (IReadOnlyDictionary<string, byte[]>)Require(fields, ProviderParametersKey),
            WrappedSecret = Bytes(fields, WrappedSecretKey, 1, MaximumWrappedSecretSize),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds((long)Require(fields, CreatedAtKey)),
            Critical = critical,
            Purpose = Text(fields, PurposeKey)
        };
    }

    private static void WriteFields(CborWriter writer, KeyEnvelopeV1 envelope, bool includeWrappedSecret)
    {
        writer.WriteStartMap(includeWrappedSecret ? 12 : 11);

        writer.WriteTextString(SchemaKey);
        writer.WriteTextString(KeyEnvelopeV1.Schema);
        writer.WriteTextString(VersionKey);
        writer.WriteInt32(KeyEnvelopeV1.SchemaVersion);
        writer.WriteTextString(EnvelopeIdKey);
        writer.WriteByteString(envelope.EnvelopeId);
        writer.WriteTextString(RepositoryIdKey);
        writer.WriteByteString(envelope.RepositoryId);
        writer.WriteTextString(EngineIdKey);
        writer.WriteTextString(KeyEnvelopeV1.EngineId);
        writer.WriteTextString(PurposeKey);
        writer.WriteTextString(envelope.Purpose);
        writer.WriteTextString(ProviderTypeKey);
        writer.WriteTextString(KeyEnvelopeV1.ProviderName(envelope.ProviderType));
        writer.WriteTextString(SuiteKey);
        writer.WriteTextString(envelope.Suite);

        writer.WriteTextString(ProviderParametersKey);
        writer.WriteStartMap(envelope.ProviderParameters.Count);
        foreach (var parameter in envelope.ProviderParameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteTextString(parameter.Key);
            writer.WriteByteString(parameter.Value);
        }

        writer.WriteEndMap();

        writer.WriteTextString(CreatedAtKey);
        writer.WriteInt64(envelope.CreatedAt.ToUnixTimeSeconds());

        writer.WriteTextString(CriticalKey);
        writer.WriteStartArray(envelope.Critical.Count);
        foreach (var field in envelope.Critical)
        {
            writer.WriteTextString(field);
        }

        writer.WriteEndArray();

        if (includeWrappedSecret)
        {
            writer.WriteTextString(WrappedSecretKey);
            writer.WriteByteString(envelope.WrappedSecret);
        }

        writer.WriteEndMap();
    }

    private static Dictionary<string, byte[]> ReadParameters(CborReader reader)
    {
        var count = reader.ReadStartMap() ?? throw new InvalidDataException("Indefinite-length maps are rejected.");
        if (count > MaximumParameters)
        {
            throw new InvalidDataException("Envelope declares too many provider parameters.");
        }

        var parameters = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var name = reader.ReadTextString();
            var value = reader.ReadByteString();
            if (value.Length > MaximumParameterSize)
            {
                throw new InvalidDataException("Provider parameter exceeds the maximum size.");
            }

            if (!parameters.TryAdd(name, value))
            {
                throw new InvalidDataException("Envelope contains a duplicate provider parameter.");
            }
        }

        reader.ReadEndMap();
        return parameters;
    }

    private static List<string> ReadCritical(CborReader reader)
    {
        var count = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite-length arrays are rejected.");
        if (count > UnderstoodCriticalFields.Count)
        {
            throw new InvalidDataException("Envelope declares too many critical fields.");
        }

        var critical = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            critical.Add(reader.ReadTextString());
        }

        reader.ReadEndArray();
        return critical;
    }

    private static object Require(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Envelope is missing a required field.");

    private static string Text(Dictionary<string, object> fields, string key) => (string)Require(fields, key);

    private static byte[] Bytes(Dictionary<string, object> fields, string key, int minimum, int maximum)
    {
        var value = (byte[])Require(fields, key);
        return value.Length >= minimum && value.Length <= maximum
            ? value
            : throw new InvalidDataException("Envelope field has an invalid length.");
    }
}
