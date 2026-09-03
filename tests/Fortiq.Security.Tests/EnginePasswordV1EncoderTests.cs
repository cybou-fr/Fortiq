using System.Text;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

public sealed class EnginePasswordV1EncoderTests
{
    [Fact]
    public void EncodesCanonicalBase64UrlWithoutPadding()
    {
        using var lease = new TestOnlyKeyLease(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        Span<byte> encoded = stackalloc byte[EnginePasswordV1Encoder.EncodedSize];

        EnginePasswordV1Encoder.Encode(lease, encoded);

        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", Encoding.ASCII.GetString(encoded));
        Assert.DoesNotContain('=', Encoding.ASCII.GetString(encoded));
    }

    [Fact]
    public void RejectsWrongSecretLength()
    {
        using var lease = new TestOnlyKeyLease(new byte[31]);

        Assert.Throws<ArgumentException>(() => EnginePasswordV1Encoder.Encode(lease, new byte[43]));
    }
}
