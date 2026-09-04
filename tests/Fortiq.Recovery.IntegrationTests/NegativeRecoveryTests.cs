using System.Diagnostics;
using Fortiq.Application;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// E2E-003, E2E-004 and E2E-005: a damaged repository, a cancelled backup and a restore that must
/// stay inside its staging directory. E2E-002 (wrong secret) needs the unlock provider and is not
/// covered while the P0 engine still runs without a password.
/// </summary>
public sealed class NegativeRecoveryTests
{
    [SkippableFact]
    public async Task DamagedRepositoryObjectFailsCheckAndRestore()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("e2e-003", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);

        var adapter = workspace.RecordingAdapter("state");
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        DamageLargestPackFile(repository);

        var check = await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None));
        Assert.DoesNotContain("succeeded", check.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.RestoreAsync(
                new RestoreSnapshot(descriptor, backup.SnapshotId, workspace.EnsureDirectory("restore"), source),
                CancellationToken.None));

        // The damaged run is recorded as evidence, and no receipt of it claims success.
        var damaged = workspace.Receipts()
            .Where(receipt => receipt.GetProperty("operation").GetString() is "check" or "restore")
            .ToArray();
        Assert.Equal(2, damaged.Length);
        Assert.All(damaged, receipt =>
        {
            Assert.Equal("failed", receipt.GetProperty("engineResult").GetString());
            Assert.NotEmpty(receipt.GetProperty("warnings").EnumerateArray());
        });
    }

    [SkippableFact]
    public async Task CancelledBackupLeavesARepositoryTheNextRunCanReconcile()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("e2e-004", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);
        CreateBulkSource(source, files: 400, fileSize: 128 * 1024);

        var cancelledAdapter = workspace.Adapter("state-cancelled");
        var descriptor = await cancelledAdapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var backup = cancelledAdapter.CreateSnapshotAsync(
            new CreateSnapshot(descriptor, source, "test-source"),
            cancellation.Token);
        await WaitUntilRepositoryIsLockedAsync(repository, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);

        // A cancelled run must not leave a snapshot behind.
        var afterCancellation = workspace.Adapter("state-next");
        await afterCancellation.ReconcileAsync(new ReconcileRepository(descriptor), CancellationToken.None);
        Assert.Empty(await afterCancellation.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));

        var recovered = await afterCancellation.CreateSnapshotAsync(
            new CreateSnapshot(descriptor, source, "test-source"),
            CancellationToken.None);
        Assert.Equal(64, recovered.SnapshotId.Length);

        var check = await afterCancellation.CheckAsync(new CheckRepository(descriptor), CancellationToken.None);
        Assert.True(check.IsHealthy);
    }

    [SkippableFact]
    public async Task RestoreOfAReparsePointStaysInsideTheStagingDirectory()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("e2e-005", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var outside = workspace.EnsureDirectory("outside");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);
        File.WriteAllText(Path.Combine(outside, "untouched.txt"), "outside the staging directory\n");
        Skip.IfNot(TryCreateJunction(Path.Combine(source, "escape"), outside), "Creating a junction is not permitted here.");

        var adapter = workspace.Adapter("state");
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        var target = workspace.EnsureDirectory("restore");

        // Restic stores the junction as a symlink to the original outside location. Whether it can
        // recreate it depends on the symlink privilege, so the engine may fail on its own or the
        // staging validation may reject the tree - but the outcome is always a failed restore with
        // an untouched target.
        var failure = await Record.ExceptionAsync(
            () => adapter.RestoreAsync(
                new RestoreSnapshot(descriptor, backup.SnapshotId, target, source),
                CancellationToken.None));

        Assert.True(
            failure is RestoreRejectedException or InvalidDataException,
            $"Unexpected restore outcome: {failure?.GetType().Name ?? "success"}");

        // Nothing was written outside, the target received nothing at all, and no staging directory
        // was left behind next to it.
        Assert.Equal("untouched.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(outside))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Empty(Directory.EnumerateDirectories(workspace.Root, ".fortiq-restore-*"));
    }

    [SkippableFact]
    public async Task RestoreRefusesATargetThatAlreadyHasContent()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("e2e-005-target", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);

        var adapter = workspace.Adapter("state");
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        var target = workspace.EnsureDirectory("restore");
        var existing = Path.Combine(target, "existing.txt");
        await File.WriteAllTextAsync(existing, "must not be overwritten\n");

        await Assert.ThrowsAsync<RestoreRejectedException>(
            () => adapter.RestoreAsync(
                new RestoreSnapshot(descriptor, backup.SnapshotId, target, source),
                CancellationToken.None));

        Assert.Equal("must not be overwritten\n", await File.ReadAllTextAsync(existing));
        Assert.Single(Directory.EnumerateFileSystemEntries(target));
    }

    private static void DamageLargestPackFile(string repository)
    {
        var pack = new DirectoryInfo(Path.Combine(repository, "data"))
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderByDescending(file => file.Length)
            .First();

        using var stream = new FileStream(pack.FullName, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(stream.Length / 2);
    }

    private static void CreateBulkSource(string source, int files, int fileSize)
    {
        var directory = Path.Combine(source, "bulk");
        Directory.CreateDirectory(directory);
        var content = new byte[fileSize];
        for (var index = 0; index < files; index++)
        {
            // Incompressible, distinct content so restic cannot finish the backup instantly.
            System.Security.Cryptography.RandomNumberGenerator.Fill(content);
            File.WriteAllBytes(Path.Combine(directory, $"bulk-{index:D4}.bin"), content);
        }
    }

    /// <summary>
    /// Waits until the backup is genuinely under way, which is when cancelling it means something.
    /// This is a precondition of the test rather than the behaviour under test, so the deadline is
    /// generous: under a loaded machine the engine can take a while to reach this point.
    /// </summary>
    private static async Task WaitUntilRepositoryIsLockedAsync(string repository, CancellationToken cancellationToken)
    {
        var locks = Path.Combine(repository, "locks");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(locks) && Directory.EnumerateFiles(locks).Any())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            "The engine never started the backup, so there was nothing to cancel. This is a timing "
            + "precondition of the test, not the behaviour it checks.");
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
