using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Fortiq.Infrastructure.Updates;

/// <summary>
/// The byte sequence TUF signatures are actually over: OLPC Canonical JSON.
/// </summary>
/// <remarks>
/// A signature verifies bytes, not meaning. Two encoders that disagree about key order or whitespace
/// produce two different byte sequences for the same document, and then a valid signature fails or -
/// far worse - an attacker picks whichever encoding makes their tampered document verify. TUF settles
/// this by signing one canonical form: object keys sorted by their UTF-16 code units, no insignificant
/// whitespace, integers only, and strings escaping nothing but backslash and double quote.
///
/// This is deliberately not <see cref="JsonSerializer"/> with sorted properties. Serializing a parsed
/// object re-encodes it through whatever the serializer believes the values are, which loses the
/// distinction that matters here - the document as written. The canonical form is produced by walking
/// the parsed tree and writing the bytes by hand.
/// </remarks>
public static class CanonicalJson
{
    /// <summary>Canonical UTF-8 bytes for <paramref name="element"/>.</summary>
    /// <exception cref="InvalidDataException">
    /// The element contains a value canonical JSON cannot represent - a fractional or exponent number,
    /// which has no single agreed spelling and so cannot be signed unambiguously.
    /// </exception>
    public static byte[] Encode(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
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
        // Sorted by ordinal comparison of the property names, which is the code-unit ordering the
        // specification calls for. A culture-aware sort would order the same document differently on
        // a different machine, and the signature would follow the machine rather than the document.
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
        // Canonical JSON escapes exactly two characters. Control characters are left as they are,
        // rather than escaped to \u form, because escaping them would be a second valid spelling of
        // the same string - which is the ambiguity the canonical form exists to remove.
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

    /// <summary>
    /// Canonical bytes for a document supplied as UTF-8 text, without keeping the parsed tree alive.
    /// </summary>
    public static byte[] Encode(ReadOnlySequence<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        using var document = JsonDocument.ParseValue(ref reader);
        return Encode(document.RootElement);
    }
}
