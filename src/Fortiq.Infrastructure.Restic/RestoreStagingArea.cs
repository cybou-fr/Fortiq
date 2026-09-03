using Fortiq.Application;

namespace Fortiq.Infrastructure.Restic;

/// <summary>
/// The engine never writes into the caller's target directly. It restores into a staging directory
/// on the same volume, validates the resulting tree, and only then promotes it. A restore that is
/// rejected or interrupted leaves the target exactly as it was.
/// </summary>
internal sealed class RestoreStagingArea : IDisposable
{
    private readonly string _target;
    private bool _promoted;

    private RestoreStagingArea(string target, string path)
    {
        _target = target;
        Path = path;
    }

    /// <summary>The directory the engine restores into.</summary>
    internal string Path { get; }

    internal static RestoreStagingArea Create(string target, Guid operationId)
    {
        var fullTarget = System.IO.Path.GetFullPath(target);
        if (Directory.Exists(fullTarget) && Directory.EnumerateFileSystemEntries(fullTarget).Any())
        {
            throw new RestoreRejectedException("The restore target must be empty.");
        }

        // A sibling of the target keeps the staging area on the same volume, so promotion is a
        // rename rather than a copy that could be observed half-finished.
        var parent = System.IO.Path.GetDirectoryName(fullTarget)
            ?? throw new RestoreRejectedException("The restore target must not be a volume root.");
        var staging = System.IO.Path.Combine(parent, $".fortiq-restore-{operationId:N}");
        Directory.CreateDirectory(staging);
        return new RestoreStagingArea(fullTarget, staging);
    }

    /// <summary>
    /// Rejects the restored tree if it contains a reparse point or symbolic link, or an entry that
    /// resolves outside the staging directory. Directories are checked before they are descended
    /// into, so validation never walks through a link itself.
    /// </summary>
    internal void Validate()
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var full = System.IO.Path.GetFullPath(entry);
                if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RestoreRejectedException("The restored tree contains an entry outside the staging directory.");
                }

                var info = new FileInfo(full);
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // P0 policy is fail closed: a link restored from a snapshot may point anywhere,
                    // including outside the target, and no policy for rewriting one is defined yet.
                    throw new RestoreRejectedException("The restored tree contains a reparse point or symbolic link.");
                }

                if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(full);
                }
            }
        }
    }

    /// <summary>Moves the validated tree into the target as a single rename.</summary>
    internal void Promote()
    {
        if (Directory.Exists(_target))
        {
            // Create() already established that it is empty.
            Directory.Delete(_target);
        }

        Directory.Move(Path, _target);
        _promoted = true;
    }

    public void Dispose()
    {
        if (_promoted || !Directory.Exists(Path))
        {
            return;
        }

        try
        {
            ClearReadOnlyAttributes(Path);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leftover staging data is inert: it is outside the target and carries no secret. It
            // must not replace the failure the caller is already being told about.
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var file = new FileInfo(path);
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }
        }
    }
}
