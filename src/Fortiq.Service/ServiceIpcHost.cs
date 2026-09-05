using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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

    public ServiceIpcHost(
        FortiqStatePaths paths,
        string engineRoot,
        IObjectStorageCredentialProvider storage,
        IScheduleStore schedules,
        ProvenRestore restore,
        HealthPublisher health,
        string? helperPath = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _engineRoot = engineRoot ?? throw new ArgumentNullException(nameof(engineRoot));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _health = health ?? throw new ArgumentNullException(nameof(health));
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

                while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line is null) break;

                    var response = await ProcessRequestAsync(line, stoppingToken);
                    var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                    await writer.WriteLineAsync(responseJson.AsMemory(), stoppingToken);
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<ServiceIpcProtocol.Response> ProcessRequestAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var req = JsonSerializer.Deserialize<ServiceIpcProtocol.Request>(requestJson, JsonOptions);
            if (req is null)
            {
                return new ServiceIpcProtocol.Response(false, "Invalid null request.");
            }

            switch (req.Command?.ToLowerInvariant())
            {
                case "ping":
                case "status":
                    return new ServiceIpcProtocol.Response(true, null, "{\"status\":\"ok\"}");

                case "provision":
                    return await HandleProvisionAsync(req.PayloadJson, cancellationToken);

                case "prove":
                    return await HandleProveAsync(req.PayloadJson, cancellationToken);

                default:
                    return new ServiceIpcProtocol.Response(false, $"Unknown IPC command: '{req.Command}'");
            }
        }
        catch (Exception ex)
        {
            return new ServiceIpcProtocol.Response(false, ex.Message);
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

        Directory.CreateDirectory(_paths.Working);

        var provisioned = await provisioner.CreateAsync(
            payload.RepositoryLocation,
            payload.KitDirectory,
            _paths.Working,
            cancellationToken,
            addDeviceUnlock: true,
            deviceKeyScope: DeviceKeyScope.Machine);

        var repositoryId = provisioned.Repository.Id.ToString();
        bool backupScheduled = true;
        string? schedulingFailure = null;

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
        }
        catch (Exception ex)
        {
            backupScheduled = false;
            schedulingFailure = $"Repository created with machine key, but scheduling failed: {ex.Message}";
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

    private async Task<BackupSchedule?> FindScheduleAsync(string repositoryId, CancellationToken cancellationToken)
    {
        BackupSchedule? byId = null;
        foreach (var schedule in await _schedules.ReadSchedulesAsync(cancellationToken))
        {
            if (string.Equals(schedule.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            {
                byId = schedule;
            }

            try
            {
                var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);
                if (string.Equals(kit.Manifest.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                {
                    return schedule;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }

        return byId;
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