namespace Fortiq.Application;

/// <summary>
/// Raised when a repository could not be unlocked. The message is deliberately constant: a caller
/// must not be able to tell a wrong secret from a missing key, and no repository or snapshot
/// metadata is revealed by the failure.
/// </summary>
public class UnlockFailedException : Exception
{
    private const string UnifiedMessage = "UnlockFailed";

    public UnlockFailedException()
        : base(UnifiedMessage)
    {
    }

    public UnlockFailedException(Exception? innerException)
        : base(UnifiedMessage, innerException)
    {
    }

    /// <summary>For subtypes that report a configuration problem rather than a failed unlock.</summary>
    protected UnlockFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Raised when a device key exists but belongs to another Windows identity.
/// </summary>
/// <remarks>
/// This one carries a message, unlike its base, and the distinction is deliberate. The constant
/// message on <see cref="UnlockFailedException"/> exists so a caller cannot tell a wrong secret from
/// a missing key. Nothing of that kind is disclosed here: that a key is user-scoped and that the
/// caller is running as a particular account are both facts the caller already has - the envelope is
/// on their disk and the account is their own. What it adds is the one thing they could not work out
/// unaided, which is that the two do not match.
/// <para>
/// A derived type rather than a separate one, so every existing handler for a failed unlock still
/// catches it.
/// </para>
/// </remarks>
public sealed class DeviceKeyIdentityException : UnlockFailedException
{
    public DeviceKeyIdentityException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A single engine invocation's credential. The secret itself never appears in the arguments: the
/// session only contributes the flags the engine needs to obtain the password out of band.
/// </summary>
public interface IEngineCredentialSession : IAsyncDisposable
{
    IReadOnlyList<string> EngineArguments { get; }

    /// <summary>
    /// Awaited after the engine process exits, so a failed or skipped handover is reported instead
    /// of being masked by the engine's own exit code.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}

public interface IEngineCredentialProvider
{
    /// <summary>
    /// Opens a credential for one engine invocation. The operation ID is the same one the receipt
    /// and the result carry, so a password handover can be correlated with the operation that
    /// needed it. It is not a secret and is safe on a command line.
    /// </summary>
    Task<IEngineCredentialSession> BeginAsync(Guid operationId, CancellationToken cancellationToken);
}
