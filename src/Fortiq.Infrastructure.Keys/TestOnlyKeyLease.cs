using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

internal sealed class TestOnlyKeyLease : IKeyLease
{
    private byte[]? _material;

    internal TestOnlyKeyLease(ReadOnlySpan<byte> material)
    {
        if (material.IsEmpty)
        {
            throw new ArgumentException("Key material cannot be empty.", nameof(material));
        }

        _material = material.ToArray();
    }

    public int Length => GetMaterial().Length;

    internal byte[] DangerousBufferForTests => _material ?? throw new ObjectDisposedException(nameof(TestOnlyKeyLease));

    public void CopyTo(Span<byte> destination)
    {
        var material = GetMaterial();
        if (destination.Length < material.Length)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        material.CopyTo(destination);
    }

    public void Dispose()
    {
        var material = Interlocked.Exchange(ref _material, null);
        if (material is not null)
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private byte[] GetMaterial() => _material ?? throw new ObjectDisposedException(nameof(TestOnlyKeyLease));
}
