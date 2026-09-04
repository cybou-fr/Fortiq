namespace Fortiq.Application;

/// <summary>How long, and how strictly, storage keeps what has been written to it.</summary>
public enum RetentionMode
{
    /// <summary>Nothing prevents deletion.</summary>
    None,

    /// <summary>Retention that a privileged identity can shorten or lift.</summary>
    Governance,

    /// <summary>Retention that nobody can shorten or lift for its duration.</summary>
    Compliance
}

/// <summary>
/// What the storage holding a repository will refuse to do. This is the difference between a backup
/// that is encrypted and a backup that survives whoever holds the credentials.
/// </summary>
public sealed record StorageProtection(
    bool Immutable,
    RetentionMode Mode,
    TimeSpan? DefaultRetention,
    bool ObjectLockCapable = false)
{
    /// <summary>Whether newly written objects receive a positive retention period by default.</summary>
    public bool DefaultRetentionActive =>
        Mode is RetentionMode.Governance or RetentionMode.Compliance
        && DefaultRetention is { } duration
        && duration > TimeSpan.Zero;

    /// <summary>Storage that promises nothing, which is what a plain directory or bucket is.</summary>
    public static StorageProtection None { get; } = new(false, RetentionMode.None, null);
}

/// <summary>Raised when storage does not protect a repository the way the caller required.</summary>
public sealed class StorageNotImmutableException : Exception
{
    public StorageNotImmutableException(string message)
        : base(message)
    {
    }

    public StorageNotImmutableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public StorageNotImmutableException()
        : base("This storage does not keep what is written to it.")
    {
    }
}

/// <summary>
/// Asks the storage itself what it will refuse. Nothing here infers protection from configuration
/// Fortiq wrote: the only answer worth having comes from the storage.
/// </summary>
public interface IStorageProtectionInspector
{
    Task<StorageProtection> InspectAsync(string repositoryLocation, CancellationToken cancellationToken);
}
