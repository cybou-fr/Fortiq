using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Fortiq.ControlPlane;

/// <summary>
/// OLPC Canonical JSON encoder for deterministic signing of control plane documents and telemetry.
/// Ensures that signatures are computed strictly over canonical UTF-8 bytes without whitespace ambiguity.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Encode(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] Encode(ReadOnlySequence<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        using var document = JsonDocument.ParseValue(ref reader);
        return Encode(document.RootElement);
    }

    public static byte[] Encode(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Encode(document.RootElement);
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, builder);
                break;

            case JsonValueKind.Array:
                WriteArray(element, builder);
                break;

            case JsonValueKind.String:
                WriteString(element.GetString() ?? string.Empty, builder);
                break;

            case JsonValueKind.Number:
                WriteNumber(element, builder);
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                throw new InvalidDataException(
                    $"Canonical JSON cannot encode a value of kind {element.ValueKind}.");
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder builder)
    {
        var properties = new List<JsonProperty>();
        foreach (var property in element.EnumerateObject())
        {
            properties.Add(property);
        }

        properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        builder.Append('{');
        for (var index = 0; index < properties.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            WriteString(properties[index].Name, builder);
            builder.Append(':');
            Write(properties[index].Value, builder);
        }

        builder.Append('}');
    }

    private static void WriteArray(JsonElement element, StringBuilder builder)
    {
        builder.Append('[');
        var first = true;
        foreach (var item in element.EnumerateArray())
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            Write(item, builder);
        }

        builder.Append(']');
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            if (character is '\\' or '"')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
    }

    private static void WriteNumber(JsonElement element, StringBuilder builder)
    {
        if (!element.TryGetInt64(out var integer))
        {
            throw new InvalidDataException(
                $"Canonical JSON admits integers only; '{element.GetRawText()}' is not one. " +
                "A fractional or exponent number has more than one spelling, so it cannot be signed unambiguously.");
        }

        builder.Append(integer.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
