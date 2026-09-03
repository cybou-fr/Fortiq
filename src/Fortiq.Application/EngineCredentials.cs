namespace Fortiq.Application;

/// <summary>
/// Raised when a repository could not be unlocked. The message is deliberately constant: a caller
/// must not be able to tell a wrong secret from a missing key, and no repository or snapshot
/// metadata is revealed by the failure.
/// </summary>
public sealed class UnlockFailedException : Exception
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
    Task<IEngineCredentialSession> BeginAsync(CancellationToken cancellationToken);
}
