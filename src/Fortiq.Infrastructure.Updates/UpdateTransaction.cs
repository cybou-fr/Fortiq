namespace Fortiq.Infrastructure.Updates;

/// <summary>What an interrupted update turned out to need when it was picked up again.</summary>
public enum UpdateRecoveryOutcome
{
    /// <summary>No interrupted update was found.</summary>
    NothingToRecover,

    /// <summary>Staged files were discarded. Nothing installed had been touched.</summary>
    StagingDiscarded,

    /// <summary>An interrupted swap was undone and the installed files are the originals again.</summary>
    RolledBack,

    /// <summary>The swap had finished; only the leftovers were cleared.</summary>
    AlreadyCommitted
}

/// <summary>
/// Replaces installed component files with staged ones, or puts back exactly what was there before.
/// </summary>
/// <remarks>
/// This is the second half of an update, and it deliberately knows nothing about where files came from
/// or whether they may be trusted: <see cref="TufTrustedMetadata"/> answers that, and content reaches
/// <see cref="StageAsync"/> only after it has. Keeping the two apart means the question "is this binary
/// authorised" is never answered by the code that is busy moving files.
///
/// The protocol is three directories on one volume:
///
/// <list type="number">
///   <item><b>staging</b> - new files are written here first, so a failure mid-write cannot produce a
///   half-written binary anywhere the system will run.</item>
///   <item><b>backup</b> - each installed file is moved here before its replacement arrives, which is
///   what makes rollback a move rather than a re-download.</item>
///   <item><b>install</b> - the staged file is moved in.</item>
/// </list>
///
/// A crash can land between any two of those moves. Recovery does not need to know where, because the
/// backup directory answers it: every file in it is a file whose original has not yet been put back,
/// so restoring all of them is correct whether one file was swapped or all of them, and correct again
/// if recovery itself is interrupted and runs a second time.
///
/// All three directories must be on the same volume as the installation. A move across volumes is a
/// copy and a delete, which is neither atomic nor cheap, and the guarantee above quietly stops holding.
/// </remarks>
public sealed class UpdateTransaction
{
    private const string StagingDirectoryName = "staging";
    private const string BackupDirectoryName = "backup";

    private readonly string _workingDirectory;
    private readonly string _installDirectory;
    private readonly UpdateIntent _intent;
    private readonly List<string> _staged = [];

    private UpdateTransaction(string workingDirectory, string installDirectory, UpdateIntent intent)
    {
        _workingDirectory = workingDirectory;
        _installDirectory = installDirectory;
        _intent = intent;
    }

    private string StagingRoot => Path.Combine(_workingDirectory, StagingDirectoryName);

    private string BackupRoot => Path.Combine(_workingDirectory, BackupDirectoryName);

    /// <summary>
    /// Opens a transaction that will replace <paramref name="relativePaths"/> under
    /// <paramref name="installDirectory"/>.
    /// </summary>
    /// <remarks>
    /// The paths are declared up front rather than discovered as they are staged, so that an update
    /// interrupted before a single file was written still records what it was going to do.
    /// </remarks>
    public static async Task<UpdateTransaction> BeginAsync(
        string workingDirectory,
        string installDirectory,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(relativePaths);

        if (relativePaths.Count == 0)
        {
            throw new ArgumentException("An update must name at least one file to replace.", nameof(relativePaths));
        }

        var working = Path.GetFullPath(workingDirectory);
        var install = Path.GetFullPath(installDirectory);

        foreach (var relativePath in relativePaths)
        {
            _ = ResolveWithin(install, relativePath);
        }

        var intent = await UpdateIntent.BeginAsync(working, install, relativePaths, cancellationToken);
        var transaction = new UpdateTransaction(working, install, intent);

        Directory.CreateDirectory(transaction.StagingRoot);
        Directory.CreateDirectory(transaction.BackupRoot);
        return transaction;
    }

    /// <summary>Writes verified content into staging under <paramref name="relativePath"/>.</summary>
    public async Task StageAsync(
        string relativePath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (!_intent.Document.RelativePaths.Contains(relativePath, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"This update did not declare '{relativePath}'. Staging a file the intent does not name " +
                "would leave a change that recovery could not undo.");
        }

        var staged = ResolveWithin(StagingRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllBytesAsync(staged, content, cancellationToken);

        if (!_staged.Contains(relativePath, StringComparer.Ordinal))
        {
            _staged.Add(relativePath);
        }
    }

    /// <summary>
    /// Moves every staged file into the installation, keeping the originals until the last one lands.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A declared file was never staged. Committing a partial set would install exactly the mixture of
    /// old and new components the release metadata exists to prevent.
    /// </exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var missing = _intent.Document.RelativePaths
            .Where(path => !_staged.Contains(path, StringComparer.Ordinal))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"This update declared {_intent.Document.RelativePaths.Count} file(s) and staged " +
                $"{_staged.Count}; '{string.Join("', '", missing)}' never arrived. " +
                "Committing now would install a mixture of releases.");
        }

        await _intent.AdvanceAsync(UpdateIntentState.Swapping, cancellationToken);

        try
        {
            foreach (var relativePath in _intent.Document.RelativePaths)
            {
                var installed = ResolveWithin(_installDirectory, relativePath);
                var staged = ResolveWithin(StagingRoot, relativePath);
                var backup = ResolveWithin(BackupRoot, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);

                // A file that is not there yet is a component being added rather than replaced. It
                // still needs a mark in the backup directory, or rollback would leave the addition
                // behind - a partial release that nothing afterwards would notice.
                if (File.Exists(installed))
                {
                    File.Move(installed, backup, overwrite: true);
                }
                else
                {
                    await File.WriteAllBytesAsync(backup + AbsentMarkerSuffix, [], cancellationToken);
                }

                File.Move(staged, installed, overwrite: true);
            }
        }
        catch
        {
            // The intent still says Swapping and the backup directory still holds the originals, so the
            // rollback here is the same one recovery would perform on the next start. Doing it now only
            // means the operator does not have to restart to get a working installation back.
            await RollbackAsync(cancellationToken);
            throw;
        }

        await _intent.AdvanceAsync(UpdateIntentState.Committed, cancellationToken);
        Cleanup();
    }

    /// <summary>Puts back every original this transaction has moved aside, and discards the staging.</summary>
    /// <remarks>
    /// The intent is left saying <see cref="UpdateIntentState.Swapping"/> until the originals are back
    /// and only then removed, so a rollback interrupted halfway is simply run again by recovery. The
    /// operation is idempotent: restoring a file that is already restored moves it onto itself.
    /// </remarks>
    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreFromBackup(_installDirectory, BackupRoot);
        Cleanup();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Finishes whatever an interrupted update left behind, and reports what that turned out to be.
    /// </summary>
    /// <remarks>
    /// Call this before starting an update and at service start-up. An installation left mid-swap runs
    /// with some components from one release and some from another, and nothing else in the system is
    /// in a position to notice.
    /// </remarks>
    public static async Task<UpdateRecoveryOutcome> RecoverAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var working = Path.GetFullPath(workingDirectory);
        var intent = await UpdateIntent.ReadAsync(working, cancellationToken);
        if (intent is null)
        {
            return UpdateRecoveryOutcome.NothingToRecover;
        }

        var staging = Path.Combine(working, StagingDirectoryName);
        var backup = Path.Combine(working, BackupDirectoryName);

        var outcome = intent.Document.State switch
        {
            // Nothing installed was touched, so there is nothing to put back - only staged files to
            // drop, which are worthless now that the release they belonged to was abandoned.
            UpdateIntentState.Staging => UpdateRecoveryOutcome.StagingDiscarded,

            UpdateIntentState.Swapping => RollBackInterrupted(intent.Document.InstallDirectory, backup),

            UpdateIntentState.Committed => UpdateRecoveryOutcome.AlreadyCommitted,

            _ => throw new InvalidDataException($"Unrecognised update state '{intent.Document.State}'.")
        };

        Delete(staging);
        Delete(backup);
        intent.Delete();
        return outcome;
    }

    private const string AbsentMarkerSuffix = ".fortiq-absent";

    private static UpdateRecoveryOutcome RollBackInterrupted(string installDirectory, string backupRoot)
    {
        RestoreFromBackup(installDirectory, backupRoot);
        return UpdateRecoveryOutcome.RolledBack;
    }

    private static void RestoreFromBackup(string installDirectory, string backupRoot)
    {
        if (!Directory.Exists(backupRoot))
        {
            return;
        }

        foreach (var backup in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(backupRoot, backup);

            // A marker records a file that did not exist before this update. Putting the original back
            // means removing what the update added, not restoring an empty file over it.
            if (relativePath.EndsWith(AbsentMarkerSuffix, StringComparison.Ordinal))
            {
                var added = ResolveWithin(
                    installDirectory,
                    relativePath[..^AbsentMarkerSuffix.Length]);

                if (File.Exists(added))
                {
                    File.Delete(added);
                }

                continue;
            }

            var installed = ResolveWithin(installDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
            File.Move(backup, installed, overwrite: true);
        }
    }

    private void Cleanup()
    {
        Delete(StagingRoot);
        Delete(BackupRoot);
        _intent.Delete();
    }

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="root"/>, refusing anything that
    /// escapes it.
    /// </summary>
    /// <remarks>
    /// The relative paths come from a targets document, which is signed - but a signature attests who
    /// wrote a name, not that the name is safe to use as a path. A target called
    /// <c>../../Windows/System32/…</c> would otherwise be written exactly where it asked to go.
    /// </remarks>
    private static string ResolveWithin(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A component path may not be empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                $"'{relativePath}' is an absolute path; component paths are relative to the installation.",
                nameof(relativePath));
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{relativePath}' resolves outside the directory it belongs to.",
                nameof(relativePath));
        }

        return full;
    }
}
