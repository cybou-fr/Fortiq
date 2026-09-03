using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

public sealed class KeyLeaseTests
{
    [Fact]
    public void LeaseDefensivelyCopiesInput()
    {
        var input = Enumerable.Repeat((byte)0x2A, 32).ToArray();
        using var lease = new TestOnlyKeyLease(input);
        input[0] = 0;
        Span<byte> copy = stackalloc byte[32];

        lease.CopyTo(copy);

        Assert.Equal(0x2A, copy[0]);
    }

    [Fact]
    public void DisposeZerosOwnedBufferAndRevokesAccess()
    {
        var lease = new TestOnlyKeyLease(Enumerable.Repeat((byte)0xA5, 32).ToArray());
        var ownedBuffer = lease.DangerousBufferForTests;

        lease.Dispose();

        Assert.All(ownedBuffer, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => lease.CopyTo(new byte[32]));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var lease = new TestOnlyKeyLease(new byte[32]);

        lease.Dispose();
        lease.Dispose();
    }
}
