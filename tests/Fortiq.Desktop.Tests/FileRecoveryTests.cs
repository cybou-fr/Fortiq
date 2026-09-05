using Fortiq.Desktop;
using Fortiq.Desktop.ViewModels;
using Fortiq.Recover;

namespace Fortiq.Desktop.Tests;

public sealed class FileRecoveryTests
{
    private static readonly RecoverySnapshot Snapshot = new("snapshot-id", DateTimeOffset.UtcNow, "C:/source");
    private static readonly FileRecoveryAccess Access = new("C:/repo", "C:/kit", "private-phrase", "access", "private-key");

    [Fact]
    public void AccessMaterialCannotBePrintedAccidentally()
    {
        Assert.DoesNotContain("private-phrase", Access.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", Access.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionRequiresSuccessfulRestoreAndCannotBeRepeated()
    {
        var backend = new StubRecovery();
        var model = new FileRecoveryViewModel(backend);
        await model.LoadAsync(Access);
        await model.RestoreAsync(Snapshot, "target");
        Assert.True(model.Completed);
        Assert.Equal("target", model.RestoredTarget);
        await model.RestoreAsync(Snapshot, "another-target");
        Assert.Equal(1, backend.RestoreCalls);
        model.Clear();
        Assert.Empty(model.Snapshots);
        Assert.Null(model.RestoredTarget);
    }

    [Fact]
    public async Task FailedRestoreLeavesTheSessionAvailableForRetry()
    {
        var backend = new StubRecovery { Failure = new IOException("Disk full") };
        var model = new FileRecoveryViewModel(backend);
        await model.LoadAsync(Access);
        await model.RestoreAsync(Snapshot, "target");
        Assert.False(model.Completed);
        Assert.Null(model.RestoredTarget);
        Assert.Contains("Disk full", model.Status, StringComparison.Ordinal);
        backend.Failure = null;
        await model.RestoreAsync(Snapshot, "new-target");
        Assert.True(model.Completed);
    }

    [Fact]
    public async Task CancellationWaitsForBackendAndNeverReportsCompletion()
    {
        var backend = new StubRecovery { WaitForCancellation = true };
        var model = new FileRecoveryViewModel(backend);
        await model.LoadAsync(Access);
        var operation = model.RestoreAsync(Snapshot, "target");
        Assert.True(model.Busy);
        Assert.Throws<InvalidOperationException>(model.Clear);
        model.Cancel();
        await operation;
        Assert.False(model.Busy);
        Assert.False(model.Completed);
        Assert.Contains("cancelled", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownSnapshotCannotBeRestored()
    {
        var backend = new StubRecovery();
        var model = new FileRecoveryViewModel(backend);
        await model.LoadAsync(Access);
        await Assert.ThrowsAsync<ArgumentException>(() => model.RestoreAsync(Snapshot with { Id = "other" }, "target"));
        Assert.Equal(0, backend.RestoreCalls);
    }

    [Fact]
    public void DestinationMustBeNewAndOutsideProtectedFolders()
    {
        var root = Directory.CreateTempSubdirectory("fortiq-destination-test-");
        try
        {
            var access = Access with { Repository = Path.Combine(root.FullName, "repo"), Kit = Path.Combine(root.FullName, "kit") };
            var snapshot = Snapshot with { SourcePath = Path.Combine(root.FullName, "source") };
            foreach (var folder in new[] { access.Repository, access.Kit, snapshot.SourcePath })
            {
                Directory.CreateDirectory(folder);
                Assert.Throws<IOException>(() => FileRecoveryAdapter.ValidateDestination(access, snapshot, Path.Combine(folder, "restored")));
            }
            Assert.Throws<IOException>(() => FileRecoveryAdapter.ValidateDestination(access, snapshot, root.FullName));
            Assert.Throws<IOException>(() => FileRecoveryAdapter.ValidateDestination(access, snapshot, "relative-path"));
            FileRecoveryAdapter.ValidateDestination(access, snapshot, Path.Combine(root.FullName, "new-restore"));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task AdapterUsesSelectedSnapshotAndPassesPhraseOnlyThroughMaterialReader()
    {
        var root = Directory.CreateTempSubdirectory("fortiq-adapter-test-");
        try
        {
            var executor = new StubExecutor();
            var adapter = new FileRecoveryAdapter("engines", "runs", _ => executor);
            var snapshots = await adapter.ListAsync(Access, CancellationToken.None);
            Assert.Single(snapshots);
            var target = Path.Combine(root.FullName, "restored");
            var result = await adapter.RestoreAsync(Access, snapshots[0], target, CancellationToken.None);
            Assert.Equal(target, result.Target);
            Assert.Equal((ulong)42, result.BytesRestored);
            Assert.Equal(Snapshot.Id, executor.Command!.SnapshotId);
            Assert.Equal(Snapshot.SourcePath, executor.Command.Source);
            Assert.DoesNotContain(Access.Mnemonic, executor.Command.ToString(), StringComparison.Ordinal);
            Assert.Equal(Access.Mnemonic, executor.Material);
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class StubExecutor : IRecoveryCommandExecutor
    {
        public RecoveryCommand? Command { get; private set; }
        public string? Material { get; private set; }
        public async Task<object> ExecuteAsync(RecoveryCommand command, IRecoveryMaterialReader material, CancellationToken token)
        {
            Command = command;
            Material = await material.ReadMnemonicAsync(token);
            return command.Operation == RecoveryOperation.Snapshots
                ? new { schema = "fortiq.recovery-snapshots", version = 1, snapshots = new[] { new { id = Snapshot.Id, createdAt = Snapshot.CreatedAt, path = Snapshot.SourcePath } } }
                : (object)new { schema = "fortiq.recovery-restore", version = 1, target = command.Target, bytesRestored = 42UL };
        }
    }

    private sealed class StubRecovery : IFileRecovery
    {
        public int RestoreCalls { get; private set; }
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public Task<IReadOnlyList<RecoverySnapshot>> ListAsync(FileRecoveryAccess access, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RecoverySnapshot>>([Snapshot]);
        public Task<IReadOnlyList<SnapshotFileItem>> ListFilesAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SnapshotFileItem>>([]);
        public async Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, string? specificPath = null, CancellationToken token = default)
        {
            RestoreCalls++;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            if (Failure is not null) throw Failure;
            return new FileRecoveryResult(target, 42);
        }
    }
}
