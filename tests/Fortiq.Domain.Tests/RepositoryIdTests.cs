using Fortiq.Domain;

namespace Fortiq.Domain.Tests;

public sealed class RepositoryIdTests
{
    [Fact]
    public void CreateProduces32RandomBytes()
    {
        var first = RepositoryId.Create();
        var second = RepositoryId.Create();

        Assert.Equal(RepositoryId.Size, first.ToArray().Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FromBytesRejectsWrongLength()
    {
        var error = Assert.Throws<ArgumentException>(() => RepositoryId.FromBytes(new byte[31]));

        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void ValueDoesNotAliasInputOrOutputBuffers()
    {
        var input = Enumerable.Repeat((byte)0x2A, RepositoryId.Size).ToArray();
        var id = RepositoryId.FromBytes(input);

        input[0] = 0;
        var output = id.ToArray();
        output[1] = 0;

        Assert.All(id.ToArray(), value => Assert.Equal(0x2A, value));
    }

    [Fact]
    public void EqualByteSequencesAreEqualValues()
    {
        var bytes = Enumerable.Range(0, RepositoryId.Size).Select(value => (byte)value).ToArray();

        Assert.Equal(RepositoryId.FromBytes(bytes), RepositoryId.FromBytes(bytes));
    }
}
