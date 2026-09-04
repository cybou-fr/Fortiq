using Fortiq.Domain;

namespace Fortiq.Application;

/// <summary>Whether a retention run also removes the data the forgotten snapshots referenced.</summary>
public enum PruneMode
{
    /// <summary>
    /// Forget the snapshots only. The data they referenced stays until something prunes it, which is
    /// the only thing immutable storage will allow.
    /// </summary>
    ForgetOnly,

    /// <summary>Forget the snapshots and delete the data no remaining snapshot needs.</summary>
    ForgetAndPrune
}

/// <summary>
/// How many snapshots to keep, in restic's terms. At least one rule must keep something: a policy
/// that keeps nothing is not a retention policy, it is deletion.
/// </summary>
public sealed record RetentionPolicy(
    int? KeepLast = null,
    int? KeepDaily = null,
    int? KeepWeekly = null,
    int? KeepMonthly = null,
    int? KeepYearly = null,
    TimeSpan? KeepWithin = null)
{
    public bool KeepsSomething =>
        KeepLast > 0 || KeepDaily > 0 || KeepWeekly > 0 || KeepMonthly > 0 || KeepYearly > 0
        || KeepWithin > TimeSpan.Zero;
}

public sealed record ApplyRetention(
    RepositoryDescriptor Repository,
    RetentionPolicy Policy,
    PruneMode Prune = PruneMode.ForgetOnly,
    Guid OperationId = default) : IOperationCommand;

/// <summary>
/// Raised when a retention run would leave a source with no snapshot at all. Retention exists to
/// bound how much history is kept, never to remove the last copy of anything.
/// </summary>
public sealed class RetentionWouldRemoveEverythingException : Exception
{
    public RetentionWouldRemoveEverythingException(string message)
        : base(message)
    {
    }

    public RetentionWouldRemoveEverythingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public RetentionWouldRemoveEverythingException()
        : base("This retention policy would leave a source with no snapshots.")
    {
    }
}

/// <summary>Raised when the storage holding a repository will not let data be deleted.</summary>
public sealed class PruneRefusedException : Exception
{
    public PruneRefusedException(string message)
        : base(message)
    {
    }

    public PruneRefusedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public PruneRefusedException()
        : base("This storage keeps what is written to it, so data cannot be pruned.")
    {
    }
}
