using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Holds key material in a buffer the lease owns and zeroes on <see cref="Dispose"/>. It is the
/// only way unwrapped secrets leave this assembly: no public API returns a raw array.
/// </summary>
public sealed class BufferKeyLease : IKeyLease
{
    private byte[]? _material;

    public BufferKeyLease(ReadOnlySpan<byte> material)
    {
        if (material.IsEmpty)
        {
            throw new ArgumentException("Key material cannot be empty.", nameof(material));
        }

        _material = material.ToArray();
    }

    public int Length => Material.Length;

    public void CopyTo(Span<byte> destination)
    {
        var material = Material;
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

    private byte[] Material => _material ?? throw new ObjectDisposedException(nameof(BufferKeyLease));
}
