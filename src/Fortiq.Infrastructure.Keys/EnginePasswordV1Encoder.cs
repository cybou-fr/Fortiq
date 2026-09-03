using System.Buffers.Text;
using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

public static class EnginePasswordV1Encoder
{
    public const int EngineUnlockSecretSize = 32;
    public const int EncodedSize = 43;

    public static void Encode(IKeyLease lease, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Length != EngineUnlockSecretSize)
        {
            throw new ArgumentException("Engine Unlock Secret must contain exactly 32 bytes.", nameof(lease));
        }

        if (destination.Length < EncodedSize)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        Span<byte> secret = stackalloc byte[EngineUnlockSecretSize];
        Span<byte> padded = stackalloc byte[44];
        try
        {
            lease.CopyTo(secret);
            var status = Base64.EncodeToUtf8(secret, padded, out var consumed, out var written);
            if (status != System.Buffers.OperationStatus.Done || consumed != secret.Length || written != padded.Length)
            {
                throw new InvalidOperationException("Failed to encode EnginePasswordV1.");
            }

            for (var index = 0; index < EncodedSize; index++)
            {
                destination[index] = padded[index] switch
                {
                    (byte)'+' => (byte)'-',
                    (byte)'/' => (byte)'_',
                    var value => value
                };
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(padded);
        }
    }
}
