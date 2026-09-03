using System.Security.Cryptography;

namespace Fortiq.Domain;

public readonly struct RepositoryId : IEquatable<RepositoryId>
{
    public const int Size = 32;

    private readonly byte[] _value;

    private RepositoryId(byte[] value) => _value = value;

    public static RepositoryId Create() => new(RandomNumberGenerator.GetBytes(Size));

    public static RepositoryId FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != Size)
        {
            throw new ArgumentException($"Repository ID must contain exactly {Size} bytes.", nameof(value));
        }

        return new RepositoryId(value.ToArray());
    }

    public byte[] ToArray() => (_value ?? throw new InvalidOperationException("Repository ID is uninitialized.")).ToArray();

    public override string ToString() => Convert.ToHexString(_value ?? throw new InvalidOperationException("Repository ID is uninitialized."));

    public bool Equals(RepositoryId other) =>
        _value is not null && other._value is not null && CryptographicOperations.FixedTimeEquals(_value, other._value);

    public override bool Equals(object? obj) => obj is RepositoryId other && Equals(other);

    public override int GetHashCode()
    {
        var value = _value ?? throw new InvalidOperationException("Repository ID is uninitialized.");
        var hash = new HashCode();
        hash.AddBytes(value);
        return hash.ToHashCode();
    }

    public static bool operator ==(RepositoryId left, RepositoryId right) => left.Equals(right);

    public static bool operator !=(RepositoryId left, RepositoryId right) => !left.Equals(right);
}
