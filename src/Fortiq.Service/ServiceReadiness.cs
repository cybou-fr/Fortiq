using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Restic;
using Fortiq.Infrastructure.Runs;
using Fortiq.Platform.Windows;
using Fortiq.Scheduling;

namespace Fortiq.Service;

public sealed record ReadinessFinding(string Check, string? ScheduleId, bool Passed, string Detail);

/// <summary>A preflight result for the identity that actually executed the checks.</summary>
public sealed record ServiceReadinessReport(
    string Schema, int Version, DateTimeOffset ProducedAt, string Account, string AccountSid,
    string StateDirectory, IReadOnlyList<ReadinessFinding> Findings)
{
    public bool Passed => Findings.Count > 0 && Findings.All(finding => finding.Passed);
    public string Scope { get; } = "Current process identity only. Preflight is not a backup, VSS capture or restore proof.";
}

/// <summary>Exercises access needed for unattended work without running backup or retention.</summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceReadiness(
    FortiqStatePaths paths, string engineRoot, string helperPath, IObjectStorageCredentialProvider storage)
{
    public async Task<ServiceReadinessReport> InspectAsync(CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var findings = new List<ReadinessFinding>();
        foreach (var directory in new[] { paths.Root, Path.Combine(paths.Root, "schedules") })
            TryCheck(findings, "state-read", null,
                () => { using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); _ = entries.MoveNext(); },
                "Can list " + directory, "Cannot list " + directory + " as this account.");
        foreach (var directory in new[] { Path.Combine(paths.Root, "state"),
            paths.Working, paths.Receipts, paths.Runs, Path.GetDirectoryName(paths.HealthReport)! })
        {
            TryCheck(findings, "state-access", null, () => ProbeDirectory(directory),
                "Can create, read and remove a probe in " + directory,
                "Cannot access " + directory + ". Prepare this state directory for the executing account.");
        }

        VerifiedEngine? verified = null;
        try
        {
            var manifest = await EngineManifestReader.ReadAsync(Path.Combine(engineRoot, "manifest.json"), cancellationToken);
            var entry = manifest.Engines.Single(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64");
            verified = await EngineBinaryVerifier.VerifyAsync(engineRoot, entry, cancellationToken);
            findings.Add(new("engine", null, true, "Pinned restic length and SHA-256 verified."));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            findings.Add(Failure("engine", null, "Supply the matching pinned win-x64 engine and manifest.", error));
        }

        using (verified)
        {
            TryCheck(findings, "password-helper", null, () => { using var pin = PinnedFile.Open(helperPath); },
                "The helper can be pinned. Its broker handshake is checked when a repository is opened.",
                "The password helper is missing or inaccessible.");

            var schedules = new FileSystemScheduleStore(paths.Schedules);
            IReadOnlyList<BackupSchedule> configured;
            try
            {
                configured = await schedules.ReadSchedulesAsync(cancellationToken);
                foreach (var issue in schedules.LastReadIssues)
                    findings.Add(new("schedule-format", null, false, "Invalid schedule file: " + issue.FileName));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                findings.Add(Failure("schedules", null, "Cannot read schedules.", error));
                configured = [];
            }

            var active = configured.Where(schedule => schedule.Enabled).ToArray();
            findings.Add(new("active-schedules", null, active.Length > 0,
                active.Length > 0 ? $"Inspecting {active.Length} enabled schedule(s)." : "No enabled schedules; unattended access has not been established."));
            foreach (var schedule in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InspectScheduleAsync(schedule, verified, findings, cancellationToken);
            }
        }

        return new("fortiq.service-readiness", 1, DateTimeOffset.UtcNow, identity.Name, identity.User!.Value, paths.Root, findings);
    }

    private async Task InspectScheduleAsync(BackupSchedule schedule, VerifiedEngine? engine,
        List<ReadinessFinding> findings, CancellationToken cancellationToken)
    {
        TryCheck(findings, "source-directory", schedule.Id,
            () => { using var entries = Directory.EnumerateFileSystemEntries(schedule.SourcePath).GetEnumerator(); _ = entries.MoveNext(); },
            "Source directory can be listed; individual file access is checked by the backup itself.",
            "Cannot list the source directory as this account.");

        if (schedule.Consistency == SourceConsistency.FileSystemSnapshot)
            findings.Add(new("vss-capture", schedule.Id, false,
                "VSS capture has not been exercised. Run scripts/Test-Vss.ps1 under the deployment's capture identity; preflight alone cannot approve this schedule."));

        OpenedRecoveryKit kit;
        try
        {
            kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);
            findings.Add(new("recovery-kit", schedule.Id, true, "Kit envelopes and hashes are readable and consistent."));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            findings.Add(Failure("recovery-kit", schedule.Id, "Recovery kit is missing, invalid or inaccessible.", error));
            return;
        }

        var device = kit.Envelopes.SingleOrDefault(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId);
        if (device is null)
        {
            findings.Add(new("device-key", schedule.Id, false, "No TPM envelope. Unattended work cannot prompt for recovery words."));
            return;
        }

        var scope = device.ProviderParameters.TryGetValue("keyScope", out var encodedScope) ? Encoding.UTF8.GetString(encodedScope) : "user";
        findings.Add(new("device-key-scope", schedule.Id, scope == "machine",
            scope == "machine" ? "Machine-scoped key; actual unlock follows." : "User-scoped key. Do not deploy this schedule to a different service identity; provision a machine-scoped device envelope."));
        try
        {
            using var lease = WindowsTpmEnvelope.Unwrap(device, device.RepositoryId);
            findings.Add(new("device-unlock", schedule.Id, true, "This account opened and unwrapped the existing device key."));
            if (engine is null)
            {
                findings.Add(new("repository-access", schedule.Id, false, "Repository access was not tested because the pinned engine is unavailable."));
                return;
            }

            RecoveryKitPolicy.CompareEngine(kit.Manifest, engine.Name, engine.Version, engine.Sha256);
            if (RepositoryLocation.IsObjectStorage(schedule.RepositoryLocation)
                && await storage.ForRepositoryAsync(schedule.RepositoryLocation, cancellationToken) is null)
                throw new InvalidOperationException("No object storage credentials.");
            using var credentials = new PasswordPipeCredentialProvider(helperPath, lease);
            var workspace = Path.Combine(paths.Working, "doctor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try
            {
                var repository = new RepositoryDescriptor(RepositoryId.FromBytes(device.RepositoryId), schedule.RepositoryLocation);
                var restic = ResticEngineFactory.Create(engine, credentials, workspace, storage);
                var registry = new FileSystemRepositoryRunRegistry(paths.Runs);
                await using var run = await registry.BeginAsync(repository.Id, OperationKind.Snapshots,
                    Guid.NewGuid(), RunExclusivity.Shared, cancellationToken);
                RecoveryKitPolicy.RequireSameRepository(kit.Manifest, (await restic.ReadRepositoryIdAsync(repository, cancellationToken)).ToArray());
                await restic.ListSnapshotsAsync(new ListSnapshots(repository), cancellationToken);
                findings.Add(new("repository-access", schedule.Id, true, "Repository identity matches and snapshots can be read through the password broker."));
            }
            finally
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            findings.Add(Failure("unattended-access", schedule.Id,
                "Cannot open this repository unattended. Check the existing key ACL, credentials, helper and repository access for the account in this report.", error));
        }
    }

    private static void ProbeDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        var probe = Path.Combine(directory, ".fortiq-doctor-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        stream.WriteByte(42);
        stream.Flush();
        stream.Position = 0;
        if (stream.ReadByte() != 42) throw new IOException("Probe read failed.");
    }

    private static void TryCheck(List<ReadinessFinding> findings, string check, string? schedule,
        Action action, string success, string remediation)
    {
        try { action(); findings.Add(new(check, schedule, true, success)); }
        catch (Exception error) when (error is not OperationCanceledException)
        { findings.Add(Failure(check, schedule, remediation, error)); }
    }

    // Diagnostic exceptions can contain provider messages or URLs. Only the type is published.
    private static ReadinessFinding Failure(string check, string? schedule, string remediation, Exception error) =>
        new(check, schedule, false, remediation + " Error type: " + error.GetType().Name);
}
