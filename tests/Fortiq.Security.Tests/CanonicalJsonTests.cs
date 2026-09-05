using System.Text;
using System.Text.Json;
using Fortiq.Infrastructure.Updates;

namespace Fortiq.Security.Tests;

/// <summary>
/// The encoding signatures are computed over. Every property here is a way two encoders could disagree
/// about one document - and a disagreement is an attacker's choice of which spelling verifies.
/// </summary>
public sealed class CanonicalJsonTests
{
    [Theory]
    [InlineData("""{"b":1,"a":2}""", """{"a":2,"b":1}""")]
    [InlineData("""{ "a" : [ 1 , 2 ] }""", """{"a":[1,2]}""")]
    [InlineData("""{"a":true,"b":null,"c":false}""", """{"a":true,"b":null,"c":false}""")]
    [InlineData("""{"Z":1,"a":1}""", """{"Z":1,"a":1}""")]
    public void ObjectsAreWrittenWithSortedKeysAndNoWhitespace(string input, string expected)
    {
        Assert.Equal(expected, Encode(input));
    }

    [Fact]
    public void TheSameDocumentWrittenTwoWaysEncodesIdentically()
    {
        // The property the whole file exists for. If these differed, one of the two spellings would
        // verify against a signature and the other would not, and the server would pick.
        Assert.Equal(
            Encode("""{"expires":"2026-01-01T00:00:00Z","version":3}"""),
            Encode("""  { "version" : 3 ,  "expires" : "2026-01-01T00:00:00Z" }  """));
    }

    [Fact]
    public void OnlyBackslashAndQuoteAreEscaped()
    {
        Assert.Equal("""{"a":"back\\slash \"quoted\""}""", Encode("""{"a":"back\\slash \"quoted\""}"""));
    }

    [Fact]
    public void AUnicodeEscapeAndItsLiteralCharacterEncodeIdentically()
    {
        // The JSON escape and the literal character are the same string, so they must produce the
        // same bytes. If they did not, a signer and a verifier that happened to spell it differently
        // would disagree about a document neither of them had altered.
        Assert.Equal(Encode(@"{""a"":""é""}"), Encode("""{"a":"é"}"""));
    }

    [Theory]
    [InlineData("""{"a":1.5}""")]
    [InlineData("""{"a":1e3}""")]
    [InlineData("""{"a":1.0}""")]
    public void ANumberThatIsNotAnIntegerIsRefusedRatherThanGuessedAt(string input)
    {
        // 1.0, 1e0 and 1 are the same value and three different byte sequences. Rather than pick one,
        // the encoder refuses: a document that cannot be canonicalised unambiguously cannot be signed.
        Assert.Throws<InvalidDataException>(() => Encode(input));
    }

    [Fact]
    public void LargeIntegersSurviveTheRoundTripUnrounded()
    {
        Assert.Equal("""{"a":9007199254740993}""", Encode("""{"a":9007199254740993}"""));
    }

    private static string Encode(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Encoding.UTF8.GetString(CanonicalJson.Encode(document.RootElement));
    }
}
