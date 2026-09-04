using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Restic;

/// <summary>
/// The engine never writes into the caller's target directly. It restores into a staging directory
/// on the same volume, the resulting tree is validated, and only then is it promoted. A restore that
/// is rejected or interrupted leaves the target exactly as it was.
/// </summary>
/// <remarks>
/// The staging directory is created fresh under an unpredictable name and, on Windows, with an
/// access control list that admits only the account Fortiq runs as. Both matter: a predictable path
/// invites another process to sit in it before the restore starts, and an inherited ACL would let
/// anyone with access to the parent directory change the tree between validation and promotion.
/// </remarks>
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

    internal static RestoreStagingArea Create(string target)
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

        // The name is random rather than derived from the operation: an operation ID travels through
        // receipts and command lines, and a path another process can predict is a path it can occupy.
        var staging = System.IO.Path.Combine(parent, $".fortiq-restore-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}");
        if (Directory.Exists(staging) || File.Exists(staging))
        {
            throw new RestoreRejectedException("The staging directory already exists.");
        }

        CreatePrivateDirectory(staging);
        return new RestoreStagingArea(fullTarget, staging);
    }

    /// <summary>
    /// Rejects the restored tree if it contains a reparse point or symbolic link, or an entry that
    /// resolves outside the directory being checked. Directories are checked before they are
    /// descended into, so validation never walks through a link itself.
    /// </summary>
    internal static void Validate(string directory)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
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
                    // Fail closed: a link restored from a snapshot may point anywhere, including
                    // outside the target, and no policy for rewriting one is defined yet.
                    throw new RestoreRejectedException("The restored tree contains a reparse point or symbolic link.");
                }

                if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(full);
                }
            }
        }
    }

    /// <summary>
    /// Validates the staged tree, moves it into the target as a single rename, and validates it again
    /// where it now lives. The second pass is what makes the check race-resistant: whatever the tree
    /// was in the staging directory, the tree the caller receives is one that was found acceptable at
    /// its final path, which no longer exists anywhere the previous path pointed.
    /// </summary>
    internal void Promote()
    {
        Validate(Path);

        if (Directory.Exists(_target))
        {
            // Create() already established that it is empty.
            Directory.Delete(_target);
        }

        Directory.Move(Path, _target);
        _promoted = true;

        try
        {
            Validate(_target);
        }
        catch
        {
            // A tree that fails validation at its final location must not be left there.
            Remove(_target);
            throw;
        }
    }

    public void Dispose()
    {
        if (!_promoted)
        {
            Remove(Path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity PrivateSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new IOException("The current account has no security identifier.");

        var security = new DirectorySecurity();
        security.SetOwner(owner);

        // No inheritance from the parent, and one entry: the account Fortiq runs as.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        return security;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            new DirectoryInfo(path).Create(PrivateSecurity());
            return;
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Deletes a tree without following what it contains: a reparse point is unlinked, never
    /// descended into, so cleanup cannot reach outside the directory it was asked to remove.
    /// </summary>
    private static void Remove(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            RemoveCore(new DirectoryInfo(directory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leftover staging data is inert: it is outside the target and carries no secret. It
            // must not replace the failure the caller is already being told about.
        }
    }

    private static void RemoveCore(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo link)
                {
                    link.Delete(recursive: false);
                }
                else
                {
                    entry.Delete();
                }

                continue;
            }

            if (entry is DirectoryInfo child)
            {
                RemoveCore(child);
                continue;
            }

            if (entry is FileInfo { IsReadOnly: true } file)
            {
                file.IsReadOnly = false;
            }

            entry.Delete();
        }

        directory.Delete(recursive: false);
    }
}
