using System.Runtime.Versioning;
using System.Text;
using Fortiq.Infrastructure.Keys;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The figures anomaly detection rests on, taken from the real engine rather than assumed.
/// </summary>
/// <remarks>
/// Detecting a source that has been rewritten in place depends entirely on the engine reporting how
/// much of a backup deduplication could not avoid writing. If the pinned restic build did not emit
/// that number, every backup would record zero, nothing would ever look unusual, and the whole
/// feature would be silently inert. That is worth a test against the real binary.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BackupMetricsTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task TheEngineReportsHowMuchDeduplicationCouldNotAvoidWriting()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("backup-metrics", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var receipts = workspace.EnsureDirectory("receipts");
        var schedule = new BackupSchedule(
            "documents",
            provisioned.Repository.Location,
            kitDirectory,
            source,
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        var backup = new UnattendedBackup(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("backup-work"),
            HelperPath,
            workspace.EnsureDirectory("runs"),
            receipts);

        var first = await backup.RunAsync(schedule, CancellationToken.None);

        // A first backup writes everything, because nothing is stored to deduplicate against.
        Assert.True(first.BytesAdded > 0, "The engine reported no added bytes for a first backup.");
        Assert.True(first.FilesChanged > 0, "The engine reported no new files for a first backup.");

        // Backing up an unchanged source again writes almost nothing. This is the baseline that makes
        // a later collapse visible, and it is the half most likely to be wrong.
        var unchanged = await backup.RunAsync(schedule, CancellationToken.None);
        Assert.True(
            unchanged.BytesAdded < first.BytesAdded / 10,
            $"An unchanged source added {unchanged.BytesAdded} bytes against {first.BytesAdded} for the first backup.");

        // Rewriting every file leaves the source the same size and defeats deduplication - the shape
        // that encryption in place produces, and the reason total size alone is not enough to see it.
        RewriteEveryFile(source);
        var rewritten = await backup.RunAsync(schedule, CancellationToken.None);

        Assert.True(
            rewritten.BytesAdded > unchanged.BytesAdded * 10,
            $"Rewriting every file added only {rewritten.BytesAdded} bytes.");

        // And the figures survive into the receipts, which is where monitoring reads them.
        var evidence = Assert.Single(await ReceiptHistory.ReadAsync(receipts, CancellationToken.None));
        Assert.Equal(3, evidence.Backups.Count);
        Assert.All(evidence.Backups, observation => Assert.True(observation.BytesProcessed > 0));
        Assert.Equal(
            (long)rewritten.BytesAdded,
            evidence.Backups[0].BytesAdded);
    }

    private static void RewriteEveryFile(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var file = new FileInfo(path);
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }

            // Content unrelated to the original and the same length, so the source keeps its size.
            var replacement = new byte[Math.Max(file.Length, 4_096)];
            System.Security.Cryptography.RandomNumberGenerator.Fill(replacement);
            File.WriteAllBytes(path, replacement);
        }

        File.WriteAllText(Path.Combine(root, "READ-ME.txt"), "rewritten", Encoding.UTF8);
    }
}
