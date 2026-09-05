using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Desktop;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Recover;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Comprehensive end-to-end integration test exercising the complete Fortiq Pilot Workflow:
/// 1. Deployment Bundle & Manifest Validation (Spec 21 & ADR-014)
/// 2. Deterministic Application Installation (Desktop, Service, Recover, Engines)
/// 3. Repository Provisioning with BIP-39 Recovery Kit and Platform Key Envelope
/// 4. Unattended Backup with Schema v2 Cryptographic Audit Ledger Hash Chaining
/// 5. Ledger Integrity Verification (SHA-256 chain continuity & zero-gap sequence)
/// 6. Size-Aware Restore Drill (ProvenRestore) and Health Report Publication
/// 7. Sovereign Disaster Recovery (Bare-metal restoration using Fortiq.Recover from kit + mnemonic)
/// 8. Tamper Resistance: Ledger modification detection and health degradation (audit-ledger-tampered)
/// </summary>
/// <remarks>
/// This is the <b>core</b> lane, and its name says so because the distinction is easy to lose. It runs
/// on a hosted runner, so it installs with <c>InstallService: false</c> and <c>ProvisionAcls: false</c>
/// and provisions a user-scoped key. Everything it proves is real; what it never touches is the set of
/// boundaries a pilot machine actually has:
///
/// <list type="bullet">
///   <item>elevated installation under UAC, with restrictive ACLs applied;</item>
///   <item>a registered Windows service running as LocalSystem;</item>
///   <item>a machine-scoped TPM key;</item>
///   <item>service IPC authorization against a real unelevated caller;</item>
///   <item>survival across a reboot, and scheduled unattended execution.</item>
/// </list>
///
/// Those belong to an installed-Windows lane that does not exist yet. Until it does, a green run here
/// is evidence about the workflow and not about the deployment - which is why the name changed from
/// PilotWorkflowEndToEndTests, a name that invited exactly the reading it could not support.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PilotCoreWorkflowTests
{
    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonCaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task CompletePilotWorkflowExecutesFromBundleDeploymentToDisasterRecoveryAndTamperDetection()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("pilot-e2e", CancellationToken.None);

        // =========================================================================
        // Stage 1: Deployment Bundle Creation & Manifest Validation
        // =========================================================================
        var bundleDir = workspace.EnsureDirectory("bundle");
        var desktopFolder = Path.Combine(bundleDir, "desktop");
        var serviceFolder = Path.Combine(bundleDir, "service");
        var recoverFolder = Path.Combine(bundleDir, "recover");
        Directory.CreateDirectory(desktopFolder);
        Directory.CreateDirectory(serviceFolder);
        Directory.CreateDirectory(recoverFolder);

        var desktopBytes = "Fortiq-Desktop-Payload"u8.ToArray();
        var serviceBytes = "Fortiq-Service-Payload"u8.ToArray();
        var recoverBytes = "Fortiq-Recover-Payload"u8.ToArray();
        var helperBytes = "Fortiq-Helper-Payload"u8.ToArray();

        File.WriteAllBytes(Path.Combine(desktopFolder, "Fortiq.Desktop.exe"), desktopBytes);
        File.WriteAllBytes(Path.Combine(desktopFolder, "Fortiq.PasswordHelper.exe"), helperBytes);
        File.WriteAllBytes(Path.Combine(serviceFolder, "Fortiq.Service.exe"), serviceBytes);
        File.WriteAllBytes(Path.Combine(recoverFolder, "Fortiq.Recover.exe"), recoverBytes);

        var bundleManifest = new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0.0",
            "win-x64",
            DateTimeOffset.UtcNow.ToString("O"),
            new[]
            {
                new InstallationManager.BundleComponentManifest("desktop", "desktop", "desktop/Fortiq.Desktop.exe", true, Convert.ToHexStringLower(SHA256.HashData(desktopBytes))),
                new InstallationManager.BundleComponentManifest("service", "service", "service/Fortiq.Service.exe", true, Convert.ToHexStringLower(SHA256.HashData(serviceBytes))),
                new InstallationManager.BundleComponentManifest("recover", "recover", "recover/Fortiq.Recover.exe", true, Convert.ToHexStringLower(SHA256.HashData(recoverBytes))),
                new InstallationManager.BundleComponentManifest("passwordHelper", "desktop", "desktop/Fortiq.PasswordHelper.exe", true, Convert.ToHexStringLower(SHA256.HashData(helperBytes)))
            });

        var manifestJson = JsonSerializer.Serialize(bundleManifest, JsonIndented);
        File.WriteAllText(Path.Combine(bundleDir, "bundle-manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(desktopFolder, "bundle-manifest.json"), manifestJson);

        // Verify DiscoverManifest accurately resolves parent bundle root from child folder
        var (discoveredRoot, discoveredManifest) = InstallationManager.DiscoverManifest(desktopFolder);
        Assert.NotNull(discoveredRoot);
        Assert.NotNull(discoveredManifest);
        Assert.Equal(Path.GetFullPath(bundleDir), Path.GetFullPath(discoveredRoot));

        // Validate bundle integrity
        InstallationManager.ValidateBundle(discoveredRoot, discoveredManifest);

        // =========================================================================
        // Stage 2: Application Installation
        // =========================================================================
        var installDir = workspace.EnsureDirectory("installed");
        var installOptions = new InstallOptions(
            TargetDirectory: installDir,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: desktopFolder,
            ProvisionAcls: false);

        await InstallationManager.InstallAsync(installOptions);

        Assert.True(File.Exists(Path.Combine(installDir, "Fortiq.Desktop.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "Fortiq.Service.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "Fortiq.Recover.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "Fortiq.PasswordHelper.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "bundle-manifest.json")));

        // =========================================================================
        // Stage 3: Repository Provisioning with Recovery Kit
        // =========================================================================
        var sourceDir = workspace.EnsureDirectory("source");
        var testFiles = TestDataset.Create(sourceDir);
        var repoDir = workspace.EnsureDirectory("repository");
        var kitDir = workspace.EnsureDirectory("kit");
        var stateDir = workspace.EnsureDirectory("state");
        var receiptsDir = workspace.EnsureDirectory("receipts");
        var runsDir = workspace.EnsureDirectory("runs");

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);
        var provisioned = await provisioner.CreateAsync(
            repoDir,
            kitDir,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None,
            addDeviceUnlock: true,
            deviceKeyScope: DeviceKeyScope.CurrentUser);

        Assert.NotNull(provisioned.RecoveryMnemonic);
        Assert.True(provisioned.DeviceUnlockAvailable);
        Assert.True(File.Exists(Path.Combine(kitDir, RecoveryKit.ManifestFileName)));

        // =========================================================================
        // Stage 4: Unattended Backup & Schema v2 Audit Ledger Chaining
        // =========================================================================
        var schedule = new BackupSchedule(
            "pilot-documents",
            provisioned.Repository.Location,
            kitDir,
            sourceDir,
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(4)));

        await WriteScheduleAsync(stateDir, schedule);
        var scheduleStore = new FileSystemScheduleStore(stateDir);

        var backupOperation = new UnattendedBackup(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("backup-work"),
            HelperPath,
            runsDir,
            receiptsDir);

        // Run Backup 1
        var backupResult1 = await backupOperation.RunAsync(schedule, CancellationToken.None);
        Assert.NotNull(backupResult1.SnapshotId);

        var receiptFiles1 = Directory.GetFiles(receiptsDir, "*.json");
        Assert.Single(receiptFiles1);

        var receipt1 = JsonSerializer.Deserialize<OperationReceipt>(await File.ReadAllTextAsync(receiptFiles1[0]), JsonCaseInsensitive)!;
        Assert.Equal(OperationReceipt.SchemaVersion, receipt1.Version);
        Assert.Equal(1, receipt1.SequenceNumber);
        Assert.Equal(OperationReceipt.GenesisHash, receipt1.PreviousReceiptHash);
        Assert.False(string.IsNullOrWhiteSpace(receipt1.ReceiptHash));

        // Modify source dataset and run Backup 2
        var addedFile = Path.Combine(sourceDir, "pilot-audit-log.txt");
        await File.WriteAllTextAsync(addedFile, "Pilot workflow execution log entry: " + Guid.NewGuid());

        var backupResult2 = await backupOperation.RunAsync(schedule, CancellationToken.None);
        Assert.NotNull(backupResult2.SnapshotId);

        var allReceipts = Directory.GetFiles(receiptsDir, "*.json")
            .Select(f => JsonSerializer.Deserialize<OperationReceipt>(File.ReadAllText(f), JsonCaseInsensitive)!)
            .OrderBy(r => r.SequenceNumber)
            .ToArray();
        Assert.Equal(2, allReceipts.Length);

        var r1 = allReceipts[0];
        var r2 = allReceipts[1];
        Assert.Equal(OperationReceipt.SchemaVersion, r2.Version);
        Assert.Equal(2, r2.SequenceNumber);
        Assert.Equal(r1.ReceiptHash, r2.PreviousReceiptHash);
        Assert.False(string.IsNullOrWhiteSpace(r2.ReceiptHash));

        // =========================================================================
        // Stage 5: Cryptographic Audit Ledger Verification
        // =========================================================================
        var ledgerReport = await AuditLedgerVerifier.VerifyLedgerAsync(receiptsDir);
        Assert.True(ledgerReport.IsValid, "Audit ledger hash-chain should be perfectly valid.");
        Assert.Equal(2, ledgerReport.TotalReceiptsVerified);
        Assert.Empty(ledgerReport.AllAnomalies);

        // =========================================================================
        // Stage 6: Restore Drill (ProvenRestore) & Health Verdict
        // =========================================================================
        var provenRestore = new ProvenRestore(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("drill-work"),
            HelperPath,
            runsDir,
            receiptsDir);

        var proof = await provenRestore.ProveAsync(schedule, CancellationToken.None);
        Assert.Equal(provisioned.Repository.Id.ToString(), proof.RepositoryId);
        Assert.True(proof.BytesRestored > 0);

        var healthPublisher = new HealthPublisher(
            scheduleStore,
            receiptsDir,
            Path.Combine(stateDir, "health", "health.json"),
            Path.Combine(stateDir, "health", "fortiq.prom"));

        var healthSummary = await healthPublisher.PublishAsync(CancellationToken.None);
        var repoHealth = Assert.Single(healthSummary.Repositories);
        Assert.DoesNotContain(repoHealth.Findings, f => f.Code == "restore-never-proven");
        Assert.Equal(HealthVerdict.Unproven, repoHealth.Verdict);

        // =========================================================================
        // Stage 7: Sovereign Disaster Recovery using Fortiq.Recover
        // =========================================================================
        // Simulate catastrophic data loss: wipe out the entire source folder
        TestDataset.MakeWritable(sourceDir);
        Directory.Delete(sourceDir, recursive: true);
        Assert.False(Directory.Exists(sourceDir));

        var restoreDir = workspace.EnsureDirectory("disaster-recovered");

        var executor = new RecoveryCommandExecutor(HelperPath);
        var memoryMaterialReader = new FixedMaterialReader(provisioned.RecoveryMnemonic);
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        var restoreExitCode = await RecoveryCli.RunAsync(
            new[]
            {
                "restore",
                "--repository", provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", kitDir,
                "--snapshot", backupResult2.SnapshotId,
                "--target", restoreDir,
                "--source", sourceDir
            },
            executor,
            memoryMaterialReader,
            stdoutWriter,
            stderrWriter,
            CancellationToken.None);

        Assert.Equal(RecoveryCli.ExitSuccess, restoreExitCode);

        // Verify restored dataset matches original hashes
        foreach (var original in testFiles)
        {
            var targetPath = Path.Combine(restoreDir, original.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(targetPath), $"Restored file missing: {original.RelativePath}");
            Assert.Equal(original.Sha256, TestDataset.HashFile(targetPath));
        }
        Assert.True(File.Exists(Path.Combine(restoreDir, "pilot-audit-log.txt")));

        // =========================================================================
        // Stage 8: Tamper Resistance Detection & Health Degradation
        // =========================================================================
        // Tamper with receipt on disk (modify version or repo id in raw json)
        var tamperedReceiptPath = Directory.GetFiles(receiptsDir, "*.json").First();
        var originalContent = await File.ReadAllTextAsync(tamperedReceiptPath);
        var tamperedContent = originalContent.Replace("\"version\": 2", "\"version\": 99", StringComparison.Ordinal);
        if (tamperedContent == originalContent)
        {
            tamperedContent = originalContent.Replace(provisioned.Repository.Id.ToString(), "tampered-repo-id", StringComparison.Ordinal);
        }
        Assert.NotEqual(originalContent, tamperedContent);
        await File.WriteAllTextAsync(tamperedReceiptPath, tamperedContent);

        // Verify that AuditLedgerVerifier detects the tampering
        var tamperedReport = await AuditLedgerVerifier.VerifyLedgerAsync(receiptsDir);
        Assert.False(tamperedReport.IsValid, "Tampered receipt must invalidate audit ledger.");
        Assert.NotEmpty(tamperedReport.AllAnomalies);

        // HealthPublisher must reflect this tampering as AtRisk with 'audit-ledger-tampered'
        var postTamperHealth = await healthPublisher.PublishAsync(CancellationToken.None);
        var tamperedRepoHealth = Assert.Single(postTamperHealth.Repositories);
        Assert.Equal(HealthVerdict.AtRisk, tamperedRepoHealth.Verdict);
        Assert.Contains(tamperedRepoHealth.Findings, f => f.Code == "audit-ledger-tampered");
    }

    private static async Task WriteScheduleAsync(string stateDirectory, BackupSchedule schedule)
    {
        var schedules = Path.Combine(stateDirectory, "schedules");
        Directory.CreateDirectory(schedules);
        var json = $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "{{schedule.Id}}",
              "repository": {{JsonSerializer.Serialize(schedule.RepositoryLocation)}},
              "kit": {{JsonSerializer.Serialize(schedule.KitDirectory)}},
              "source": {{JsonSerializer.Serialize(schedule.SourcePath)}},
              "sourceStableId": "{{schedule.SourceStableId}}",
              "recurrence": { "kind": "interval", "period": "04:00:00" }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(schedules, schedule.Id + ".json"), json);
    }

    private sealed class FixedMaterialReader(string mnemonic) : IRecoveryMaterialReader
    {
        public Task<string> ReadMnemonicAsync(CancellationToken token) => Task.FromResult(mnemonic);
    }
}
