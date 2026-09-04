namespace Fortiq.Application;

/// <summary>
/// What a repository looks like when objects have been hidden rather than destroyed. Immutable
/// storage refuses to delete a version, but it permits a delete that names only a key: that leaves a
/// marker on top and the object stops being visible while every version of it survives underneath.
/// </summary>
public sealed record HiddenObjects(int HiddenCount, int RecoverableCount, bool VersioningAvailable)
{
    /// <summary>Nothing is hidden, which is what a healthy repository looks like.</summary>
    public static HiddenObjects None { get; } = new(0, 0, true);

    public bool AnythingHidden => HiddenCount > 0;
}

/// <summary>What an attempt to bring hidden objects back actually did.</summary>
public sealed record HiddenObjectsRestored(int RestoredCount, int StillHiddenCount);

/// <summary>
/// Finds objects that have been hidden behind delete markers, and brings them back. This is the
/// difference between storage that cannot lose data and a repository that cannot be read: object
/// locking guarantees the first, and nothing but this restores the second.
/// </summary>
public interface IHiddenObjectRecovery
{
    Task<HiddenObjects> InspectAsync(string repositoryLocation, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the delete markers hiding a repository's objects, so the versions underneath become
    /// current again. It never removes a version that holds data.
    /// </summary>
    Task<HiddenObjectsRestored> RestoreAsync(string repositoryLocation, CancellationToken cancellationToken);
}
