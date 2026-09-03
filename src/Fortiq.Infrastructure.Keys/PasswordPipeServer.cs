using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Fortiq.Application;
using Fortiq.Platform.Windows;

namespace Fortiq.Infrastructure.Keys;

public static class PasswordPipeProtocol
{
    public const int ChallengeSize = 32;
    public const int PasswordSize = EnginePasswordV1Encoder.EncodedSize + 1;

    public static string PipeName(Guid id) => id == Guid.Empty
        ? throw new ArgumentException("Empty operation ID.", nameof(id))
        : $"fortiq-password-v1-{id:N}";

    public static byte[] Respond(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != ChallengeSize)
        {
            throw new ArgumentException("Invalid challenge.", nameof(challenge));
        }

        var label = "fortiq/password-helper/v1"u8;
        Span<byte> input = stackalloc byte[label.Length + ChallengeSize];
        label.CopyTo(input);
        challenge.CopyTo(input[label.Length..]);
        return SHA256.HashData(input);
    }
}

/// <summary>
/// What the broker requires of the process it hands the engine password to.
/// </summary>
/// <param name="HelperPath">
/// The only image allowed to receive the password. It is pinned open, so the file that was approved
/// is the file that must be running.
/// </param>
/// <param name="ExpectedUser">
/// The account the client must be running as. Defaults to the account the broker itself runs as,
/// which is what a service passing the password to its own helper requires.
/// </param>
/// <param name="PipeSecurityDescriptor">
/// An SDDL string, normally supplied by the installer, describing exactly who may open the pipe.
/// When it is absent the pipe is restricted to the current user by the operating system instead.
/// </param>
public sealed record PasswordBrokerOptions(
    string HelperPath,
    SecurityIdentifier? ExpectedUser = null,
    string? PipeSecurityDescriptor = null);

/// <summary>
/// Serves the engine password exactly once, to exactly one process, over a pipe that exists for one
/// operation. Before a single byte of the password is written, the broker checks that the connected
/// client is the pinned helper image and runs as the expected account.
/// </summary>
internal sealed class PasswordPipeServer
{
    private readonly Guid _operationId;
    private readonly IKeyLease _lease;
    private readonly PinnedFile _helper;
    private readonly PasswordBrokerOptions _options;

    internal PasswordPipeServer(Guid operationId, IKeyLease lease, PinnedFile helper, PasswordBrokerOptions options)
    {
        _operationId = operationId;
        _lease = lease;
        _helper = helper;
        _options = options;
    }

    internal async Task ServeOnceAsync(CancellationToken token)
    {
        using var pipe = CreatePipe();
        await pipe.WaitForConnectionAsync(token);

        // The process is checked before the challenge is even offered, so a client that is not the
        // approved helper never sees anything but a closed pipe.
        AuthorizeProcess(pipe);

        var challenge = RandomNumberGenerator.GetBytes(PasswordPipeProtocol.ChallengeSize);
        var response = new byte[PasswordPipeProtocol.ChallengeSize];
        var expected = PasswordPipeProtocol.Respond(challenge);
        var password = new byte[PasswordPipeProtocol.PasswordSize];
        try
        {
            await pipe.WriteAsync(challenge, token);
            await pipe.FlushAsync(token);
            await pipe.ReadExactlyAsync(response, token);
            if (!CryptographicOperations.FixedTimeEquals(response, expected))
            {
                throw new InvalidDataException("Invalid helper response.");
            }

            // Windows only allows impersonating the client once data has been read from the pipe,
            // so the account is checked here - still before any part of the password is written.
            AuthorizeUser(pipe);

            EnginePasswordV1Encoder.Encode(_lease, password);
            password[^1] = (byte)'\n';
            await pipe.WriteAsync(password, token);
            await pipe.FlushAsync(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            CryptographicOperations.ZeroMemory(response);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var name = PasswordPipeProtocol.PipeName(_operationId);
        if (_options.PipeSecurityDescriptor is not { Length: > 0 } sddl)
        {
            // Without an installer-defined descriptor the operating system restricts the pipe to the
            // current user, which the client checks below then narrow further.
            return new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A pipe security descriptor is supported on Windows only.");
        }

        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private void AuthorizeProcess(NamedPipeServerStream pipe)
    {
        var imagePath = NamedPipeClientInspector.ImagePathOf(pipe);
        if (!_helper.IsSameFileAs(imagePath))
        {
            throw new UnauthorizedAccessException(
                "The process that connected to the password pipe is not the approved helper.");
        }
    }

    private void AuthorizeUser(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = _options.ExpectedUser ?? CurrentUser();
        if (expected is null)
        {
            return;
        }

        var client = NamedPipeClientInspector.UserOf(pipe);
        if (client is null || !expected.Equals(client))
        {
            throw new UnauthorizedAccessException(
                "The process that connected to the password pipe runs as a different account.");
        }
    }

    private static SecurityIdentifier? CurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }
}
