using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Infrastructure.Restic;

internal sealed class ResticRepositoryEngine : IBackupRepository
{
    private readonly VerifiedEngine _engine;
    private readonly IResticProcessRunner _runner;
    private readonly string _workingDirectory;

    internal ResticRepositoryEngine(VerifiedEngine engine, IResticProcessRunner runner, string workingDirectory)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public async Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var location = NormalizePath(command.Location);
        var result = await RunAsync(ResticOperation.Initialize, ["--repo", location, "--insecure-no-password", "--json"], cancellationToken);
        var initialized = ResticJsonParser.ParseInitialized(result);
        return new RepositoryDescriptor(RepositoryId.FromBytes(Convert.FromHexString(initialized.Id)), location);
    }

    public async Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationId = Guid.NewGuid();
        var result = await RunAsync(
            ResticOperation.Backup,
            [NormalizePath(command.SourcePath), "--repo", NormalizePath(command.Repository.Location), "--insecure-no-password", "--json", "--no-cache"],
            cancellationToken);
        var summary = ResticJsonParser.ParseBackup(result);
        return new BackupReceipt(operationId, command.Repository.Id, summary.SnapshotId);
    }

    public async Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = await RunAsync(
            ResticOperation.Snapshots,
            ["--repo", NormalizePath(query.Repository.Location), "--insecure-no-password", "--json", "--no-cache"],
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
            ["--repo", NormalizePath(command.Repository.Location), "--insecure-no-password", "--json", "--no-cache"],
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
            [command.SnapshotId, "--target", target, "--repo", NormalizePath(command.Repository.Location), "--insecure-no-password", "--json", "--no-cache"],
            cancellationToken);
        ResticJsonParser.ParseRestore(result);
        return new RestoreReceipt(operationId, command.Repository.Id, command.SnapshotId, target);
    }

    private Task<ResticProcessResult> RunAsync(ResticOperation operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _runner.RunAsync(_engine, new ResticProcessRequest(operation, arguments, _workingDirectory), cancellationToken);

    private static string NormalizePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Path.GetFullPath(value);
    }
}
