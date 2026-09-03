using System.IO.Pipes;
using System.Security.Cryptography;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.PasswordHelper;

public static class PasswordHelperClient
{
    public static async Task RunAsync(Guid operationId, Stream output, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", PasswordPipeProtocol.PipeName(operationId), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(token);
        var challenge = new byte[32]; var password = new byte[PasswordPipeProtocol.PasswordSize]; byte[]? response = null;
        try
        {
            await pipe.ReadExactlyAsync(challenge, token); response = PasswordPipeProtocol.Respond(challenge);
            await pipe.WriteAsync(response, token); await pipe.FlushAsync(token); await pipe.ReadExactlyAsync(password, token);
            if (password[^1] != (byte)'\n') throw new InvalidDataException("Password is not newline terminated.");
            await output.WriteAsync(password, token); await output.FlushAsync(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge); CryptographicOperations.ZeroMemory(password);
            if (response is not null) CryptographicOperations.ZeroMemory(response);
        }
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !Guid.TryParseExact(args[0], "D", out var id) || id == Guid.Empty) return 64;
        try { await PasswordHelperClient.RunAsync(id, Console.OpenStandardOutput(), CancellationToken.None); return 0; }
        catch { return 1; }
    }
}
