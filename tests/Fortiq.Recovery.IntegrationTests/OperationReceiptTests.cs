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
        Assert.Equal("succeeded", root.GetProperty("result").GetString());
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
        Assert.Equal("failed", root.GetProperty("result").GetString());
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
        Assert.Equal("cancelled", document.RootElement.GetProperty("result").GetString());
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

    private ReceiptRecordingBackupRepository Decorate(IBackupRepository inner) =>
        new(inner, Engine, new FileSystemOperationReceiptStore(_directory));

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
    }
}
