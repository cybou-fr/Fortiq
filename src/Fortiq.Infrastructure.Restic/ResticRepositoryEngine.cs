using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Infrastructure.Restic;

internal sealed class ResticRepositoryEngine : IBackupRepository
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
        var location = NormalizePath(command.Location);
        var result = await RunAsync(ResticOperation.Initialize, ["--repo", location, "--json"], cancellationToken);
        var initialized = ResticJsonParser.ParseInitialized(result);
        return new RepositoryDescriptor(RepositoryId.FromBytes(Convert.FromHexString(initialized.Id)), location);
    }

    public async Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = Guid.NewGuid();
        var result = await RunAsync(
            ResticOperation.Backup,
            [NormalizePath(command.SourcePath), "--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
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
            cancellationToken);
        return ResticJsonParser.ParseSnapshots(result)
            .Select(snapshot => new SnapshotDescriptor(snapshot.Id, snapshot.Time, snapshot.Paths.Count == 0 ? string.Empty : snapshot.Paths[0]))
            .ToArray();
    }

    public async Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = Guid.NewGuid();
        var result = await RunAsync(
            ResticOperation.Check,
            ["--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
            cancellationToken);
        var summary = ResticJsonParser.ParseCheck(result);
        return new CheckReceipt(operationId, command.Repository.Id, summary.IsHealthy);
    }

    public async Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = Guid.NewGuid();
        var target = NormalizePath(command.TargetPath);
        var result = await RunAsync(
            ResticOperation.Restore,
            [RestoreSelector(command), "--target", target, "--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
            cancellationToken);
        var summary = ResticJsonParser.ParseRestore(result);
        return new RestoreReceipt(
            operationId,
            command.Repository.Id,
            command.SnapshotId,
            target,
            summary.FilesRestored,
            summary.BytesRestored);
    }

    public async Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await RunAsync(
            ResticOperation.Unlock,
            ["--repo", NormalizePath(command.Repository.Location), "--json", "--no-cache"],
            cancellationToken);
        ResticJsonParser.ParseUnlock(result);
    }

    private async Task<ResticProcessResult> RunAsync(
        ResticOperation operation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // The credential session is opened per invocation and torn down with it, so a secret is
        // never reusable by a later process.
        await using var credential = await _credentials.BeginAsync(cancellationToken);
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
