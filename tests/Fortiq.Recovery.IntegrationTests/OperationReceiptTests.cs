using System.Text.Json;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Receipts;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Receipts are written for successful and for failed operations, follow the documented schema and
/// never claim success on behalf of an operation that did not finish.
/// </summary>
public sealed class OperationReceiptTests : IDisposable
{
    private static readonly EngineIdentity Engine = new("restic", "0.19.1", new string('a', 64));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-receipts-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuccessfulBackupProducesAReceiptThatMatchesTheDocumentedSchema()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var recorded = Decorate(new StubRepository(new BackupReceipt(Guid.NewGuid(), repository.Id, new string('b', 64), 7, 4096)));

        await recorded.CreateSnapshotAsync(new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source"), CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SingleReceipt()));
        var root = document.RootElement;
        Assert.Equal("fortiq.operation-receipt", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("backup", root.GetProperty("operation").GetString());
        Assert.Equal("succeeded", root.GetProperty("engineResult").GetString());
        Assert.Equal(repository.Id.ToString(), root.GetProperty("repositoryId").GetString());
        Assert.Equal(new string('b', 64), root.GetProperty("snapshotId").GetString());
        Assert.Equal("directory", root.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("test-source", root.GetProperty("source").GetProperty("stableId").GetString());
        Assert.Equal(7, root.GetProperty("metrics").GetProperty("filesProcessed").GetInt64());
        Assert.Equal(4096, root.GetProperty("metrics").GetProperty("bytesProcessed").GetInt64());
        Assert.Equal("0.19.1", root.GetProperty("engine").GetProperty("version").GetString());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.True(root.GetProperty("completedAt").GetDateTimeOffset() >= root.GetProperty("startedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task FailedOperationIsRecordedAsFailedAndKeepsTheOriginalError()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var recorded = Decorate(new StubRepository(new InvalidDataException("Restic operation failed with exit code 1.")));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => recorded.CheckAsync(new CheckRepository(repository), CancellationToken.None));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SingleReceipt()));
        var root = document.RootElement;
        Assert.Equal("check", root.GetProperty("operation").GetString());
        Assert.Equal("failed", root.GetProperty("engineResult").GetString());
        Assert.False(root.TryGetProperty("snapshotId", out _));
        Assert.Contains("exit code 1", Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task CancelledOperationIsRecordedAsCancelledRatherThanFailed()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var recorded = Decorate(new StubRepository(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recorded.CreateSnapshotAsync(new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source"), CancellationToken.None));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SingleReceipt()));
        Assert.Equal("cancelled", document.RootElement.GetProperty("engineResult").GetString());
    }

    [Fact]
    public async Task TheOperationIdOfTheCallerReachesTheResultAndTheReceipt()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var operationId = Guid.NewGuid();
        var inner = new EchoRepository();
        var recorded = Decorate(inner);

        var result = await recorded.CreateSnapshotAsync(
            new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source", OperationId: operationId),
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SingleReceipt()));
        Assert.Equal(operationId, inner.LastCommand!.OperationId);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetGuid());
    }

    [Fact]
    public async Task AnOperationWithoutAnIdIsAssignedOneThatIsUsedEverywhere()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var inner = new EchoRepository();
        var recorded = Decorate(inner);

        var result = await recorded.CreateSnapshotAsync(
            new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source"),
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SingleReceipt()));
        Assert.NotEqual(Guid.Empty, inner.LastCommand!.OperationId);
        Assert.Equal(inner.LastCommand.OperationId, result.OperationId);
        Assert.Equal(inner.LastCommand.OperationId, document.RootElement.GetProperty("operationId").GetGuid());
    }

    [Fact]
    public async Task EvidenceIsWrittenEvenWhenTheCallerCancelsAfterTheEngineFinished()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var observer = new RecordingObserver();
        var recorded = Decorate(new EchoRepository(), observer);

        // The engine already did the work; a cancelled caller token must neither suppress the
        // evidence nor rewrite what the engine did.
        var result = await recorded.CreateSnapshotAsync(
            new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source"),
            cancellation.Token);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SingleReceipt()));
        Assert.Equal("succeeded", document.RootElement.GetProperty("engineResult").GetString());
        Assert.Equal(result.OperationId, document.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(EvidenceWriteResult.Succeeded, Assert.Single(observer.Evidence).WriteResult);
    }

    [Fact]
    public async Task AFailedEvidenceWriteIsReportedSeparatelyFromTheEngineResult()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        await File.WriteAllTextAsync(_directory, "a file where the receipt directory should be");

        var observer = new RecordingObserver();
        var recorded = Decorate(new EchoRepository(), observer);

        await recorded.CreateSnapshotAsync(
            new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source"),
            CancellationToken.None);

        var evidence = Assert.Single(observer.Evidence);
        Assert.Equal(EngineResult.Succeeded, evidence.Receipt.EngineResult);
        Assert.Equal(EvidenceWriteResult.Failed, evidence.WriteResult);
        Assert.NotNull(evidence.WriteError);
    }

    [Fact]
    public async Task AnUnwritableReceiptDirectoryDoesNotFailTheOperation()
    {
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var receipt = new BackupReceipt(Guid.NewGuid(), repository.Id, new string('c', 64));

        // A file where the receipt directory should be makes every write fail.
        await File.WriteAllTextAsync(_directory, "not a directory");
        var recorded = Decorate(new StubRepository(receipt));

        var result = await recorded.CreateSnapshotAsync(
            new CreateSnapshot(repository, Path.GetFullPath("source"), "test-source"),
            CancellationToken.None);

        Assert.Equal(receipt.SnapshotId, result.SnapshotId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        else if (File.Exists(_directory))
        {
            File.Delete(_directory);
        }
    }

    private ReceiptRecordingBackupRepository Decorate(
        IBackupRepository inner,
        IOperationEvidenceObserver? observer = null) =>
        new(inner, Engine, new FileSystemOperationReceiptStore(_directory), observer);

    /// <summary>An engine stand-in that reports the command it was given back to the test.</summary>
    private sealed class EchoRepository : IBackupRepository
    {
        internal CreateSnapshot? LastCommand { get; private set; }

        public Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(new BackupReceipt(command.OperationId, command.Repository.Id, new string('d', 64), 1, 2));
        }

        public Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RetentionReceipt> ApplyRetentionAsync(ApplyRetention command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingObserver : IOperationEvidenceObserver
    {
        private readonly List<OperationEvidence> _evidence = [];

        internal IReadOnlyList<OperationEvidence> Evidence => _evidence;

        public void OnEvidence(OperationEvidence evidence) => _evidence.Add(evidence);
    }

    private string SingleReceipt() => Assert.Single(Directory.GetFiles(_directory, "*.json"));

    private sealed class StubRepository : IBackupRepository
    {
        private readonly BackupReceipt? _backup;
        private readonly Exception? _failure;

        internal StubRepository(BackupReceipt backup) => _backup = backup;

        internal StubRepository(Exception failure) => _failure = failure;

        public Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken) =>
            _failure is null ? Task.FromResult(_backup!) : Task.FromException<BackupReceipt>(_failure);

        public Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken) =>
            _failure is null ? throw new NotSupportedException() : Task.FromException<CheckReceipt>(_failure);

        public Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RetentionReceipt> ApplyRetentionAsync(ApplyRetention command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
