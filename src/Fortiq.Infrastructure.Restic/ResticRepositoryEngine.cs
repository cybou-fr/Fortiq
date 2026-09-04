using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Infrastructure.Restic;

internal sealed class ResticRepositoryEngine : IRepositoryEngine
{
    private readonly VerifiedEngine _engine;
    private readonly IResticProcessRunner _runner;
    private readonly IEngineCredentialProvider _credentials;
    private readonly string _workingDirectory;

    internal ResticRepositoryEngine(
        VerifiedEngine engine,
        IResticProcessRunner runner,
        IEngineCredentialProvider credentials,
        string workingDirectory)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public async Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = OperationId(command);
        var location = NormalizePath(command.Location);
        var result = await RunAsync(ResticOperation.Initialize, ["--repo", location, "--json"], operationId, cancellationToken);
        var initialized = ResticJsonParser.ParseInitialized(result);
        return new RepositoryDescriptor(RepositoryId.FromBytes(Convert.FromHexString(initialized.Id)), location);
    }

    public async Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = OperationId(command);
        var result = await RunAsync(
            ResticOperation.Backup,
            [
                NormalizePath(command.SourcePath),
                // The stable source identity is written into the repository, so a recovery on a
                // clean machine can tell what a snapshot is without any local Fortiq state.
                "--tag", ResticSnapshotMetadata.TagArgument(command.SourceStableId),
                "--repo", NormalizePath(command.Repository.Location),
                "--json",
                "--no-cache"
            ],
            operationId,
            cancellationToken);
        var summary = ResticJsonParser.ParseBackup(result);
        return new BackupReceipt(
            operationId,
            command.Repository.Id,
            summary.SnapshotId,
            summary.TotalFilesProcessed,
            summary.TotalBytesProcessed);
    }

    public async Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = await RunAsync(
            ResticOperation.Snapshots,
            ["--repo", NormalizePath(query.Repository.Location), "--json", "--no-cache"],
            OperationId(query),
            cancellationToken);
        return ResticJsonParser.ParseSnapshots(result)
            .Select(snapshot => new SnapshotDescriptor(
                snapshot.Id,
                snapshot.Time,
                ResticSnapshotMetadata.ReadSourceStableId(snapshot.Tags),
                snapshot.Paths.Count == 0 ? string.Empty : snapshot.Paths[0]))
            .ToArray();
    }

    public async Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = OperationId(command);
        var result = await RunAsync(
            ResticOperation.Check,
            ["--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
            operationId,
            cancellationToken);
        var summary = ResticJsonParser.ParseCheck(result);
        return new CheckReceipt(operationId, command.Repository.Id, summary.IsHealthy);
    }

    public async Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = OperationId(command);
        var target = NormalizePath(command.TargetPath);

        // The engine restores into a staging area; the target only ever receives a tree that passed
        // validation, and it receives it as one rename.
        using var staging = RestoreStagingArea.Create(target);
        var result = await RunAsync(
            ResticOperation.Restore,
            [RestoreSelector(command), "--target", staging.Path, "--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
            operationId,
            cancellationToken);
        var summary = ResticJsonParser.ParseRestore(result);
        staging.Promote();
        return new RestoreReceipt(
            operationId,
            command.Repository.Id,
            command.SnapshotId,
            target,
            summary.FilesRestored,
            summary.BytesRestored);
    }

    public async Task<RepositoryId> ReadRepositoryIdAsync(RepositoryDescriptor repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var result = await RunAsync(
            ResticOperation.CatConfig,
            ["config", "--repo", NormalizePath(repository.Location), "--json", "--no-cache"],
            Guid.NewGuid(),
            cancellationToken);
        return RepositoryId.FromBytes(Convert.FromHexString(ResticJsonParser.ParseConfig(result).Id));
    }

    public async Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // --remove-all also clears a lock whose owner cannot be proven dead: a killed run can leave
        // a lock whose PID has already been reused, and the plain stale-lock check then keeps the
        // repository unusable. Reconciliation is therefore only valid when no other Fortiq
        // operation is in flight; enforcing that with a run registry is a P1 gate.
        var result = await RunAsync(
            ResticOperation.Unlock,
            ["--remove-all", "--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
            OperationId(command),
            cancellationToken);
        ResticJsonParser.ParseUnlock(result);
    }

    /// <summary>
    /// The operation ID the caller supplied, or a new one when the caller did not identify the
    /// operation. Whatever this returns is what the engine invocation, the password handover and
    /// the returned receipt all use.
    /// </summary>
    private static Guid OperationId(IOperationCommand command) =>
        command.OperationId == Guid.Empty ? Guid.NewGuid() : command.OperationId;

    private async Task<ResticProcessResult> RunAsync(
        ResticOperation operation,
        IReadOnlyList<string> arguments,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        // The credential session is opened per invocation and torn down with it, so a secret is
        // never reusable by a later process.
        await using var credential = await _credentials.BeginAsync(operationId, cancellationToken);
        var result = await _runner.RunAsync(
            _engine,
            new ResticProcessRequest(
                operation,
                [.. arguments, .. credential.EngineArguments],
                _workingDirectory,
                CreateEngineEnvironment()),
            cancellationToken);

        await credential.CompleteAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Restic needs a writable temporary directory for the pack files it assembles before upload.
    /// Fortiq supplies one inside its own working directory instead of inheriting the ambient TEMP,
    /// and adds only the Windows root required by the platform APIs restic calls.
    /// </summary>
    private Dictionary<string, string> CreateEngineEnvironment()
    {
        var temporaryDirectory = Path.Combine(_workingDirectory, "tmp");
        Directory.CreateDirectory(temporaryDirectory);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEMP"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory
        };

        if (Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } systemRoot)
        {
            environment["SystemRoot"] = systemRoot;
        }

        return environment;
    }

    /// <summary>
    /// Builds the restic restore selector. Restic addresses a subtree as <c>snapshot:/C/path</c> and
    /// currently accepts only forward slashes in that position.
    /// </summary>
    private static string RestoreSelector(RestoreSnapshot command)
    {
        if (command.SourcePath is null)
        {
            return command.SnapshotId;
        }

        var full = NormalizePath(command.SourcePath);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Source path must be rooted.", nameof(command));
        var drive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':');
        var relative = full[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
        return $"{command.SnapshotId}:/{drive}/{relative}";
    }

    private static string NormalizePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Path.GetFullPath(value);
    }
}
