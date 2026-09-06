using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Fortiq.Platform.Windows;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.ObjectStorage;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;
using Microsoft.Extensions.Hosting;

namespace Fortiq.Service;

/// <summary>
/// Background service that hosts the privileged Named Pipe endpoint for local IPC (ADR-007, Spec 15).
/// Standard desktop users delegate machine-scoped TPM repository provisioning and privileged restore
/// drills to this host, preventing standard users from needing direct write access to %ProgramData%\Fortiq\work.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceIpcHost : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FortiqStatePaths _paths;
    private readonly string _engineRoot;
    private readonly string _helperPath;
    private readonly IObjectStorageCredentialProvider _storage;
    private readonly IScheduleStore _schedules;
    private readonly ProvenRestore _restore;
    private readonly HealthPublisher _health;
    private readonly ScheduledBackupRunner _backups;
    private readonly StaleLockRecovery _locks;

    public ServiceIpcHost(
        FortiqStatePaths paths,
        string engineRoot,
        IObjectStorageCredentialProvider storage,
        IScheduleStore schedules,
        ProvenRestore restore,
        HealthPublisher health,
        ScheduledBackupRunner backups,
        StaleLockRecovery locks,
        string? helperPath = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _engineRoot = engineRoot ?? throw new ArgumentNullException(nameof(engineRoot));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunWindowsServerAsync(stoppingToken);
    }

    private async Task RunWindowsServerAsync(CancellationToken stoppingToken)
    {
        var pipeSecurity = CreatePipeSecurity();

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    ServiceIpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    pipeSecurity);

                await server.WaitForConnectionAsync(stoppingToken);

                _ = HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                await Task.Delay(100, stoppingToken);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                // One request per connection, which is what the client has always done - every call it
                // makes opens a pipe of its own. It is stated here rather than left implied, because
                // watching for a cancellation takes over this connection's reader: a second request
                // read from the same connection would be racing the watcher for the same bytes.
                if (stoppingToken.IsCancellationRequested || !pipe.IsConnected)
                {
                    return;
                }

                var line = await reader.ReadLineAsync(stoppingToken);
                if (line is null)
                {
                    return;
                }

                var response = await ProcessCancellableAsync(line, pipe, reader, stoppingToken);
                var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                await writer.WriteLineAsync(responseJson.AsMemory(), stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                // Reading the request, serialising the answer and writing it back all happen out here,
                // and until now every failure among them was dropped without a word: the caller saw a
                // connection close and there was nothing, anywhere, saying why. A service whose value
                // is trustworthy diagnostics cannot be the quietest thing on the machine when it
                // breaks.
                LogConnectionFailure(error);
            }
        }
    }

    /// <summary>
    /// Runs one request, and stops it if the caller asks or goes away while it is running.
    /// </summary>
    /// <remarks>
    /// A backup of a large folder takes minutes and could not be stopped: the desktop's only recourse
    /// was to close and let the work continue invisibly to the end. What was missing was not a button
    /// but a way for one to mean anything - the request had already been read, and nothing was
    /// listening to the pipe until the answer went back down it.
    ///
    /// Something is listening now. Anything the caller writes while the operation runs, and the pipe
    /// breaking because they closed it, both mean the same thing: stop. Reading is safe here precisely
    /// because the protocol has the caller silent until it is answered, and the client opens a
    /// connection per request - so this connection has nothing else to carry, and it is closed after a
    /// cancellable operation rather than returned to the loop with its reader half consumed.
    ///
    /// Cancellation stops the engine by killing its process, which leaves that run's lock behind in
    /// the repository. That is why "clear the lock" exists and why it is offered on the same screen.
    /// </remarks>
    private async Task<ServiceIpcProtocol.Response> ProcessCancellableAsync(
        string requestJson,
        NamedPipeServerStream pipe,
        StreamReader reader,
        CancellationToken stoppingToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // The read is not awaited here. It completes when the caller says stop, or never - and the
        // reader is disposed with the connection either way.
        var watching = Task.Run(async () =>
        {
            try
            {
                // Null is the pipe closing; anything else is the caller asking. Neither needs
                // interpreting: nothing else may be sent while a request is outstanding.
                await reader.ReadLineAsync(stoppingToken);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The connection ending is the signal, not a fault to report.
            }

            if (!operation.IsCancellationRequested)
            {
                await operation.CancelAsync();
            }
        }, CancellationToken.None);

        try
        {
            return await ProcessRequestAsync(requestJson, pipe, operation.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The caller's own request. Reported as an outcome rather than as a failure of the
            // service, and said in the words the person will read.
            return new ServiceIpcProtocol.Response(false,
                "The operation was stopped before it finished. Nothing was recorded for it, and the "
                + "repository may be holding the lock of the interrupted run - clear it from the "
                + "source's settings if the next backup says the repository is locked.");
        }
        finally
        {
            // Whatever happened, this connection's reader has been taken over and the watcher is
            // still holding it. Both end here.
            await operation.CancelAsync();
            _ = watching;
        }
    }

    private async Task<ServiceIpcProtocol.Response> ProcessRequestAsync(
        string requestJson,
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            var req = JsonSerializer.Deserialize<ServiceIpcProtocol.Request>(requestJson, JsonOptions);
            if (req is null)
            {
                return new ServiceIpcProtocol.Response(false, "Invalid null request.");
            }

            // Between reading the envelope and touching the payload. Windows will not disclose the
            // client's identity until they have written something, so authorising at connection time
            // is not available; what is available, and what matters, is authorising before the
            // privileged payload is interpreted rather than after it has been acted on.
            var authorization = Authorize(req.Command, pipe);
            if (!authorization.Allowed)
            {
                return new ServiceIpcProtocol.Response(false, authorization.Denial);
            }

            switch (req.Command?.ToLowerInvariant())
            {
                case "ping":
                case "status":
                    return new ServiceIpcProtocol.Response(
                        true,
                        null,
                        JsonSerializer.Serialize(
                            new ServiceIpcProtocol.StatusResponse("ok", FortiqVersion.Current),
                            JsonOptions));

                case "provision":
                    return await HandleProvisionAsync(req.PayloadJson, cancellationToken);

                case "prove":
                    return await HandleProveAsync(req.PayloadJson, cancellationToken);

                case "backup":
                    return await HandleBackupAsync(req.PayloadJson, cancellationToken);

                case "updateschedule":
                    return await HandleUpdateScheduleAsync(req.PayloadJson, cancellationToken);

                case "removeschedule":
                    return await HandleRemoveScheduleAsync(req.PayloadJson, cancellationToken);

                case "clearlock":
                    return await HandleClearLockAsync(req.PayloadJson, cancellationToken);

                default:
                    return new ServiceIpcProtocol.Response(false, $"Unknown IPC command: '{req.Command}'");
            }
        }
        catch (Exception ex)
        {
            // The caller gets no detail and the machine's administrator gets all of it. Returning
            // ex.Message alone dropped the inner exception and the stack on the floor, so a failed
            // provision reached the screen as the single word "UnlockFailed" - which is deliberately
            // uninformative, being the unified message that keeps an attacker from learning which
            // unlock factor failed - and there was nowhere at all to find out why. A product whose
            // value is trustworthy diagnostics cannot answer "it did not work" and nothing else.
            LogFailure(requestJson, ex);
            return new ServiceIpcProtocol.Response(false, Explain(ex));
        }
    }

    /// <summary>A sentence the person can act on, without saying more than the caller may know.</summary>
    private static string Explain(Exception error) => error switch
    {
        UnlockFailedException => "Fortiq could not unlock the repository it just created, so nothing was " +
            "kept. The reason is recorded in the Windows event log under the 'Fortiq' source - open " +
            "Event Viewer, or ask an administrator to.",
        _ => error.Message
    };

    /// <summary>
    /// Records why a request failed where an administrator of this machine can read it.
    /// </summary>
    /// <remarks>
    /// The event log and not a file: it is already the place Windows services report to, it survives
    /// the service being reinstalled, and it is not writable by the accounts this service refuses.
    /// The command is recorded, never the payload - a provision payload names paths, and a failure
    /// report is not a reason to copy them somewhere with different permissions.
    /// </remarks>
    /// <summary>Records a failure that happened around a request rather than inside one.</summary>
    private static void LogConnectionFailure(Exception error)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var log = new EventLog("Application") { Source = "Fortiq" };
            log.WriteEntry(
                $"An IPC connection failed outside request handling.{Environment.NewLine}{error}",
                EventLogEntryType.Warning);
        }
        catch (Exception logging) when (logging is System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A service that cannot write its log still has to keep serving the next caller.
        }
    }

    private static void LogFailure(string requestJson, Exception error)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var command = "unknown";
        try
        {
            command = JsonSerializer.Deserialize<ServiceIpcProtocol.Request>(requestJson, JsonOptions)?.Command ?? "unknown";
        }
        catch (JsonException)
        {
            // The request could not be read, which the entry below still records as a failure.
        }

        try
        {
            using var log = new EventLog("Application") { Source = "Fortiq" };
            log.WriteEntry(
                $"IPC command '{command}' failed.{Environment.NewLine}{error}",
                EventLogEntryType.Error);
        }
        catch (Exception logging) when (logging is System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A service that cannot write its log must still answer the request it was given.
        }
    }

    private async Task<ServiceIpcProtocol.Response> HandleProvisionAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ServiceIpcProtocol.Response(false, "Missing provision payload.");
        }

        var payload = JsonSerializer.Deserialize<ServiceIpcProtocol.ProvisionPayload>(payloadJson, JsonOptions);
        if (payload is null)
        {
            return new ServiceIpcProtocol.Response(false, "Failed to deserialize provision payload.");
        }

        var provisioner = new RepositoryProvisioner(
            _engineRoot,
            passwordHelperPath: _helperPath,
            storage: _storage,
            protection: new S3StorageProtectionInspector(_storage));

        // A directory of its own for this attempt. The provisioning intent that guards a half-finished
        // run lives in the working directory, and every provision used to share one - so a single
        // interrupted attempt left an intent that refused every later attempt, for any repository,
        // permanently. The person seeing that refusal cannot clear it either: the directory is closed
        // to them by design. Per-run directories keep the guard doing its job - recognising one
        // unfinished repository - without it becoming a lock on the whole feature.
        var working = Path.Combine(_paths.Working, "provision", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);

        var provisioned = await provisioner.CreateAsync(
            payload.RepositoryLocation,
            payload.KitDirectory,
            working,
            cancellationToken,
            addDeviceUnlock: true,
            deviceKeyScope: DeviceKeyScope.Machine);

        var repositoryId = provisioned.Repository.Id.ToString();
        bool backupScheduled = false;
        string? schedulingFailure = null;

        if (!provisioned.DeviceUnlockAvailable)
        {
            backupScheduled = false;
            // Not "this machine has no TPM": the service cannot see one from here either way. What
            // it knows is that the machine key store would not give it a key, and that a schedule
            // without one would be a promise of unattended backups it could never keep.
            schedulingFailure =
                "A device key could not be created in the machine key store, so unattended backups have "
                + "nothing to unlock the repository with and no schedule was created. Your backups are "
                + "not affected: the recovery phrase still opens this repository.";
        }
        else
        {
            try
            {
                await FileSystemScheduleStore.WriteDefaultScheduleAsync(
                    Path.Combine(_paths.Schedules, "schedules"),
                    repositoryId,
                    payload.RepositoryLocation,
                    payload.KitDirectory,
                    payload.SourcePath,
                    new TimeOnly(2, 30),
                    cancellationToken);
                backupScheduled = true;
            }
            catch (Exception ex)
            {
                backupScheduled = false;
                schedulingFailure = $"Repository created with machine key, but scheduling failed: {ex.Message}";
            }
        }

        var provisionResp = new ServiceIpcProtocol.ProvisionResponse(
            repositoryId,
            provisioned.RecoveryMnemonic,
            provisioned.DeviceUnlockAvailable,
            backupScheduled,
            schedulingFailure);

        return new ServiceIpcProtocol.Response(true, null, JsonSerializer.Serialize(provisionResp, JsonOptions));
    }

    private async Task<ServiceIpcProtocol.Response> HandleProveAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ServiceIpcProtocol.Response(false, "Missing prove payload.");
        }

        var payload = JsonSerializer.Deserialize<ServiceIpcProtocol.ProvePayload>(payloadJson, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RepositoryId))
        {
            return new ServiceIpcProtocol.Response(false, "Invalid prove payload.");
        }

        var schedule = await FindScheduleAsync(payload.RepositoryId, cancellationToken);
        if (schedule is null)
        {
            return new ServiceIpcProtocol.Response(false, $"No schedule found on machine for repository '{payload.RepositoryId}'.");
        }

        bool proven = false;
        string? errorMsg = null;
        try
        {
            await _restore.ProveAsync(schedule, cancellationToken);
            proven = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var state = await _schedules.ReadStateAsync(schedule.DrillStateId, CancellationToken.None);
            await _schedules.WriteStateAsync(state with
            {
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastFailure = error.Message
            }, CancellationToken.None);

            errorMsg = error.Message;
            if (error is not RestoreProofFailedException)
            {
                throw;
            }
        }
        finally
        {
            await _health.PublishAsync(cancellationToken);
        }

        var proveResp = new ServiceIpcProtocol.ProveResponse(proven, errorMsg);
        return new ServiceIpcProtocol.Response(true, null, JsonSerializer.Serialize(proveResp, JsonOptions));
    }

    /// <summary>Backs up one repository now, because somebody asked rather than because it is due.</summary>
    /// <remarks>
    /// The same runner the scheduler uses, so a manual backup and a nightly one are one code path with
    /// one history. Health is published afterwards whatever happened: the screen that asked for this
    /// is the screen that has to show what came of it, and a backup whose result only appears at the
    /// next poll looks like a button that did nothing.
    /// </remarks>
    private async Task<ServiceIpcProtocol.Response> HandleBackupAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ServiceIpcProtocol.Response(false, "Missing backup payload.");
        }

        var payload = JsonSerializer.Deserialize<ServiceIpcProtocol.BackupPayload>(payloadJson, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RepositoryId))
        {
            return new ServiceIpcProtocol.Response(false, "Invalid backup payload.");
        }

        // The desktop knows repositories by their repository id; schedules are keyed by their own id,
        // and the two are only the same when the schedule was written by provisioning. Resolving
        // through the kit is what makes the button work for a schedule somebody wrote by hand.
        var schedule = await FindScheduleAsync(payload.RepositoryId, cancellationToken);
        if (schedule is null)
        {
            return new ServiceIpcProtocol.Response(false, $"No schedule found on machine for repository '{payload.RepositoryId}'.");
        }

        ScheduleRunOutcome outcome;
        try
        {
            outcome = await _backups.RunNowAsync(schedule.Id, cancellationToken);
        }
        finally
        {
            await _health.PublishAsync(cancellationToken);
        }

        var backupResp = new ServiceIpcProtocol.BackupResponse(
            outcome.Failure is null && outcome.SnapshotId is not null,
            outcome.SnapshotId,
            outcome.Failure);

        return new ServiceIpcProtocol.Response(true, null, JsonSerializer.Serialize(backupResp, JsonOptions));
    }

    /// <summary>Applies the settings somebody chose on the source's screen.</summary>
    /// <remarks>
    /// The schedules directory is readable by any user on the machine and writable only by the
    /// service, its administrators and SYSTEM - which is why showing these settings needs nothing and
    /// changing them comes through here. Health is republished afterwards so the screen reflects a
    /// paused schedule immediately rather than at the next poll.
    /// </remarks>
    private async Task<ServiceIpcProtocol.Response> HandleUpdateScheduleAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ServiceIpcProtocol.Response(false, "Missing schedule payload.");
        }

        var payload = JsonSerializer.Deserialize<ServiceIpcProtocol.SchedulePreferencesPayload>(payloadJson, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RepositoryId))
        {
            return new ServiceIpcProtocol.Response(false, "Invalid schedule payload.");
        }

        var schedule = await FindScheduleAsync(payload.RepositoryId, cancellationToken);
        if (schedule is null)
        {
            return new ServiceIpcProtocol.Response(false, $"No schedule found on machine for repository '{payload.RepositoryId}'.");
        }

        var store = _schedules as FileSystemScheduleStore
            ?? throw new InvalidOperationException("This machine's schedules are not stored in a form this service can edit.");

        await store.UpdateAsync(schedule.Id, SchedulePreferencesFrom(payload), cancellationToken);
        await _health.PublishAsync(cancellationToken);
        return new ServiceIpcProtocol.Response(true);
    }

    /// <summary>Stops protecting a source, and does not touch what has already been backed up.</summary>
    private async Task<ServiceIpcProtocol.Response> HandleRemoveScheduleAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ServiceIpcProtocol.Response(false, "Missing schedule payload.");
        }

        var payload = JsonSerializer.Deserialize<ServiceIpcProtocol.RemoveSchedulePayload>(payloadJson, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RepositoryId))
        {
            return new ServiceIpcProtocol.Response(false, "Invalid schedule payload.");
        }

        var schedule = await FindScheduleAsync(payload.RepositoryId, cancellationToken);
        if (schedule is null)
        {
            return new ServiceIpcProtocol.Response(false, $"No schedule found on machine for repository '{payload.RepositoryId}'.");
        }

        var store = _schedules as FileSystemScheduleStore
            ?? throw new InvalidOperationException("This machine's schedules are not stored in a form this service can edit.");

        await store.RemoveAsync(schedule.Id, cancellationToken);
        await _health.PublishAsync(cancellationToken);
        return new ServiceIpcProtocol.Response(true);
    }

    /// <summary>Turns the wire's plain numbers back into the domain's own vocabulary.</summary>
    internal static SchedulePreferences SchedulePreferencesFrom(ServiceIpcProtocol.SchedulePreferencesPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var minutes = Math.Clamp(payload.BackupMinuteOfDay, 0, (24 * 60) - 1);
        var retention = payload.KeepDaily is null && payload.KeepWeekly is null && payload.KeepMonthly is null
            ? null
            : new RetentionPolicy(
                KeepDaily: payload.KeepDaily,
                KeepWeekly: payload.KeepWeekly,
                KeepMonthly: payload.KeepMonthly);

        return new SchedulePreferences(
            payload.Enabled,
            new TimeOnly(minutes / 60, minutes % 60),
            payload.DrillEveryDays is { } days and > 0 ? TimeSpan.FromDays(days) : null,
            retention,
            payload.Prune ? PruneMode.ForgetAndPrune : PruneMode.ForgetOnly);
    }

    /// <summary>Clears the lock an interrupted run left in a repository.</summary>
    /// <remarks>
    /// Privileged like every other operation that opens a repository, and refused outright while
    /// another Fortiq run on this machine holds it - clearing a lock under a running backup would
    /// remove that backup's own lock.
    /// </remarks>
    private async Task<ServiceIpcProtocol.Response> HandleClearLockAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ServiceIpcProtocol.Response(false, "Missing lock payload.");
        }

        var payload = JsonSerializer.Deserialize<ServiceIpcProtocol.ClearLockPayload>(payloadJson, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RepositoryId))
        {
            return new ServiceIpcProtocol.Response(false, "Invalid lock payload.");
        }

        var schedule = await FindScheduleAsync(payload.RepositoryId, cancellationToken);
        if (schedule is null)
        {
            return new ServiceIpcProtocol.Response(false, $"No schedule found on machine for repository '{payload.RepositoryId}'.");
        }

        try
        {
            await _locks.ClearAsync(schedule, cancellationToken);
        }
        catch (RepositoryBusyException busy)
        {
            // Not an error to be explained away: it is the guard working. The person is told to wait
            // rather than shown a failure they might repeat until it happens to get through.
            return new ServiceIpcProtocol.Response(false,
                "Fortiq is working on this repository right now, so its locks were left alone. "
                + $"Wait for that to finish and try again. ({busy.Message})");
        }

        await _health.PublishAsync(cancellationToken);
        return new ServiceIpcProtocol.Response(true);
    }

    private Task<BackupSchedule?> FindScheduleAsync(string repositoryId, CancellationToken cancellationToken) =>
        ScheduleLookup.FindForRepositoryAsync(_schedules, repositoryId, cancellationToken);

    /// <summary>
    /// Decides whether the client on <paramref name="pipe"/> may issue <paramref name="command"/>.
    /// </summary>
    /// <remarks>
    /// A failure to resolve the caller is a refusal, not a pass. An identity that cannot be read is
    /// not a trusted one, and the alternative - proceeding because the check itself broke - turns
    /// every fault in this path into an open door.
    /// </remarks>
    private static ServiceIpcAuthorizationResult Authorize(string? command, NamedPipeServerStream pipe)
    {
        if (ServiceIpcAuthorization.TrustFor(command) == ServiceIpcCommandTrust.Public)
        {
            return ServiceIpcAuthorizationResult.Allow;
        }

        try
        {
            var principal = NamedPipeClientInspector.PrincipalOf(pipe);
            return ServiceIpcAuthorization.Authorize(
                command,
                principal.IsAdministrator,
                principal.IsOperator,
                principal.AccountName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ServiceIpcAuthorizationResult.Deny(
                $"The caller of '{command}' could not be identified, so the request was refused: {error.Message}");
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}