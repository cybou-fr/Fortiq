using Fortiq.Domain;

namespace Fortiq.Application;

/// <summary>How a run relates to other runs against the same repository.</summary>
public enum RunExclusivity
{
    /// <summary>Runs alongside other shared runs; the engine arbitrates the repository itself.</summary>
    Shared,

    /// <summary>
    /// Requires that no other Fortiq run is in flight against this repository. Reconciliation needs
    /// this: it removes locks whose owner cannot be proven dead, which is only safe when nothing
    /// else is running.
    /// </summary>
    Exclusive
}

/// <summary>Raised when a run cannot start because another run holds the repository.</summary>
public sealed class RepositoryBusyException : Exception
{
    public RepositoryBusyException(string message)
        : base(message)
    {
    }

    public RepositoryBusyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public RepositoryBusyException()
        : base("Another Fortiq run is working on this repository.")
    {
    }
}

/// <summary>A run in flight. Disposing it releases the repository for other runs.</summary>
public interface IRepositoryRun : IAsyncDisposable
{
    Guid OperationId { get; }

    RunExclusivity Exclusivity { get; }
}

/// <summary>
/// Knows which Fortiq runs are working on a repository right now, across processes. It is what makes
/// "no other operation is in flight" a fact that can be established rather than assumed.
/// </summary>
public interface IRepositoryRunRegistry
{
    Task<IRepositoryRun> BeginAsync(
        RepositoryId repository,
        OperationKind operation,
        Guid operationId,
        RunExclusivity exclusivity,
        CancellationToken cancellationToken);
}
