using System.IO.Pipes;
using System.Security.Cryptography;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

public static class PasswordPipeProtocol
{
    public const int ChallengeSize = 32;
    public const int PasswordSize = EnginePasswordV1Encoder.EncodedSize + 1;
    public static string PipeName(Guid id) => id == Guid.Empty ? throw new ArgumentException("Empty operation ID.", nameof(id)) : $"fortiq-password-v1-{id:N}";
    public static byte[] Respond(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != ChallengeSize) throw new ArgumentException("Invalid challenge.", nameof(challenge));
        var label = "fortiq/password-helper/v1"u8;
        Span<byte> input = stackalloc byte[label.Length + ChallengeSize];
        label.CopyTo(input);
        challenge.CopyTo(input[label.Length..]);
        return SHA256.HashData(input);
    }
}

internal sealed class TestOnlyPasswordPipeServer(Guid operationId, IKeyLease lease)
{
    internal async Task ServeOnceAsync(CancellationToken token)
    {
        using var pipe = new NamedPipeServerStream(PasswordPipeProtocol.PipeName(operationId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(token);
        var challenge = RandomNumberGenerator.GetBytes(32); var response = new byte[32];
        var expected = PasswordPipeProtocol.Respond(challenge); var password = new byte[PasswordPipeProtocol.PasswordSize];
        try
        {
            await pipe.WriteAsync(challenge, token); await pipe.FlushAsync(token); await pipe.ReadExactlyAsync(response, token);
            if (!CryptographicOperations.FixedTimeEquals(response, expected)) throw new InvalidDataException("Invalid helper response.");
            EnginePasswordV1Encoder.Encode(lease, password); password[^1] = (byte)'\n';
            await pipe.WriteAsync(password, token); await pipe.FlushAsync(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge); CryptographicOperations.ZeroMemory(response);
            CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(password);
        }
    }
}
