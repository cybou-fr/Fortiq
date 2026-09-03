using Fortiq.Application;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// E2E-001: dataset → repository → backup → deletion of the temporary Fortiq state → restore on a
/// clean working directory → content verification.
/// </summary>
public sealed class EndToEndRecoveryTests
{
    [SkippableFact]
    public async Task RestoresDatasetAfterLocalStateIsDeleted()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("e2e-001", CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        var target = workspace.EnsureDirectory("restore");
        var expected = TestDataset.Create(source);

        var backupState = workspace.EnsureDirectory("state-backup");
        var backupAdapter = workspace.RecordingAdapter("state-backup");

        var descriptor = await backupAdapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await backupAdapter.CreateSnapshotAsync(
            new CreateSnapshot(descriptor, source, "test-source"),
            CancellationToken.None);

        Assert.Equal(64, backup.SnapshotId.Length);

        // Everything Fortiq kept outside the repository is destroyed before the restore.
        Directory.Delete(backupState, recursive: true);

        var restoreAdapter = workspace.RecordingAdapter("state-restore");

        var snapshots = await restoreAdapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None);
        Assert.Contains(snapshots, snapshot => snapshot.Id == backup.SnapshotId);

        var check = await restoreAdapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None);
        Assert.True(check.IsHealthy);

        var restore = await restoreAdapter.RestoreAsync(
            new RestoreSnapshot(descriptor, backup.SnapshotId, target, source),
            CancellationToken.None);

        foreach (var entry in expected)
        {
            var restored = Path.Combine(restore.TargetPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(restored), $"Missing restored file: {entry.RelativePath}");
            Assert.Equal(entry.Sha256, TestDataset.HashFile(restored));
        }

        var restoredCount = Directory.EnumerateFiles(restore.TargetPath, "*", SearchOption.AllDirectories).Count();
        Assert.Equal(expected.Count, restoredCount);

        // Every operation left evidence, and the backup receipt names the snapshot that was verified.
        var receipts = workspace.Receipts();
        Assert.Equal(
            ["backup", "check", "initialize", "restore", "snapshots"],
            receipts.Select(receipt => receipt.GetProperty("operation").GetString()!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(receipts, receipt => Assert.Equal("succeeded", receipt.GetProperty("result").GetString()));
        var backupReceipt = receipts.Single(receipt => receipt.GetProperty("operation").GetString() == "backup");
        Assert.Equal(backup.SnapshotId, backupReceipt.GetProperty("snapshotId").GetString());
        Assert.Equal(expected.Count, backupReceipt.GetProperty("metrics").GetProperty("filesProcessed").GetInt64());
    }
}
