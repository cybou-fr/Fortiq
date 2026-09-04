using System.Security.Principal;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Reading the source from a filesystem snapshot instead of the live filesystem. The privilege it
/// needs is not always there, and a backup that could not take a snapshot must not be recorded as
/// though it had.
/// </summary>
public sealed class SourceConsistencyTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    private static bool HasBackupPrivileges
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    [SkippableFact]
    public async Task WithoutBackupPrivilegesASnapshotBackupFailsRatherThanFallingBackToLive()
    {
        Skip.If(HasBackupPrivileges, "This session can create volume snapshots, so there is nothing to refuse.");
        using var workspace = await RecoveryWorkspace.CreateAsync("consistency-refused", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var repository = workspace.EnsureDirectory("repository");
        var adapter = workspace.Adapter("state");
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.CreateSnapshotAsync(
                new CreateSnapshot(descriptor, source, "test-source", SourceConsistency.FileSystemSnapshot),
                CancellationToken.None));

        // The engine says why, and the repository holds no snapshot claiming to be point in time.
        Assert.Contains("VSS", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));
    }

    [SkippableFact]
    public async Task ALiveBackupSaysSoInTheRepository()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");
        using var workspace = await RecoveryWorkspace.CreateAsync("consistency-live", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var repository = workspace.EnsureDirectory("repository");
        var adapter = workspace.Adapter("state");
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        // Recorded in the repository, not in a local file: a recovery has to be able to tell what a
        // snapshot is without any Fortiq state.
        var snapshot = Assert.Single(await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));
        Assert.False(snapshot.PointInTime);
        Assert.Equal("test-source", snapshot.SourceStableId);
    }

    [SkippableFact]
    public async Task WithBackupPrivilegesTheSnapshotIsPointInTimeAndSaysSo()
    {
        Skip.IfNot(HasBackupPrivileges, "Creating a volume snapshot needs backup privileges.");
        using var workspace = await RecoveryWorkspace.CreateAsync("consistency-snapshot", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var expected = TestDataset.Create(source);

        var repository = workspace.EnsureDirectory("repository");
        var adapter = workspace.Adapter("state");
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);

        var backup = await adapter.CreateSnapshotAsync(
            new CreateSnapshot(descriptor, source, "test-source", SourceConsistency.FileSystemSnapshot),
            CancellationToken.None);

        var snapshot = Assert.Single(await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));
        Assert.True(snapshot.PointInTime);
        Assert.Equal("test-source", snapshot.SourceStableId);

        // The engine takes the volume snapshot itself, so what the repository records is the source's
        // own path rather than a shadow copy device path - which is what makes the restore below
        // address the same source a live backup would.
        Assert.Equal(source, snapshot.SourcePath);

        var target = workspace.EnsureDirectory("restored");
        await adapter.RestoreAsync(
            new RestoreSnapshot(descriptor, backup.SnapshotId, target, source),
            CancellationToken.None);

        foreach (var entry in expected)
        {
            var file = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(entry.Sha256, TestDataset.HashFile(file));
        }
    }
}
