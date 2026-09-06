using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Fortiq.Application;

namespace Fortiq.Desktop;

public interface IServiceIpcClient
{
    Task<bool> IsServiceAvailableAsync(CancellationToken cancellationToken = default);
    Task<ServiceIpcProtocol.ProvisionResponse> ProvisionAsync(string repositoryLocation, string kitDirectory, string sourcePath, CancellationToken cancellationToken = default);
    Task<bool> ProveRecoveryAsync(string repositoryId, CancellationToken cancellationToken = default);
    Task<ServiceIpcProtocol.BackupResponse> BackupAsync(string repositoryId, CancellationToken cancellationToken = default);
    Task UpdateScheduleAsync(string repositoryId, ViewModels.SourceSettings settings, CancellationToken cancellationToken = default);
    Task RemoveScheduleAsync(string repositoryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Client communicating with the privileged background Windows Service over Named Pipes.
/// Standard desktop users delegate machine-scoped operations (machine TPM key provisioning,
/// restore drills, and receipts) to the service, keeping sensitive work out of user-writable space.
/// </summary>
public sealed class ServiceIpcClient : IServiceIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;

    public ServiceIpcClient(string pipeName = ServiceIpcProtocol.PipeName)
    {
        _pipeName = pipeName;
    }

    public async Task<bool> IsServiceAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));

            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var req = new ServiceIpcProtocol.Request("ping");
            await writer.WriteLineAsync(JsonSerializer.Serialize(req, JsonOptions).AsMemory(), cts.Token);

            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) return false;

            var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions);
            return resp is not null && resp.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ServiceIpcProtocol.ProvisionResponse> ProvisionAsync(
        string repositoryLocation,
        string kitDirectory,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Service IPC is only supported on Windows.");
        }

        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cancellationToken);

        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var payload = new ServiceIpcProtocol.ProvisionPayload(repositoryLocation, kitDirectory, sourcePath);
        var req = new ServiceIpcProtocol.Request("provision", JsonSerializer.Serialize(payload, JsonOptions));

        await writer.WriteLineAsync(JsonSerializer.Serialize(req, JsonOptions).AsMemory(), cancellationToken);

        var line = await ReadOperationResponseAsync(reader, cancellationToken)
            ?? throw new InvalidOperationException("Service closed connection unexpectedly during provisioning.");

        var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!resp.Success)
        {
            throw new InvalidOperationException(resp.ErrorMessage ?? "Service failed to provision repository.");
        }

        if (resp.PayloadJson is null)
        {
            throw new InvalidOperationException("Service returned empty provision payload.");
        }

        return JsonSerializer.Deserialize<ServiceIpcProtocol.ProvisionResponse>(resp.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to decode service provision response.");
    }

    public async Task<bool> ProveRecoveryAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Service IPC is only supported on Windows.");
        }

        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cancellationToken);

        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var payload = new ServiceIpcProtocol.ProvePayload(repositoryId);
        var req = new ServiceIpcProtocol.Request("prove", JsonSerializer.Serialize(payload, JsonOptions));

        await writer.WriteLineAsync(JsonSerializer.Serialize(req, JsonOptions).AsMemory(), cancellationToken);

        var line = await ReadOperationResponseAsync(reader, cancellationToken)
            ?? throw new InvalidOperationException("Service closed connection unexpectedly during restore drill.");

        var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!resp.Success)
        {
            throw new InvalidOperationException(resp.ErrorMessage ?? "Service failed to execute restore drill.");
        }

        if (resp.PayloadJson is null)
        {
            return true;
        }

        var proveResp = JsonSerializer.Deserialize<ServiceIpcProtocol.ProveResponse>(resp.PayloadJson, JsonOptions);
        return proveResp is null || proveResp.Success;
    }
    public async Task<ServiceIpcProtocol.BackupResponse> BackupAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Service IPC is only supported on Windows.");
        }

        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cancellationToken);

        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var payload = new ServiceIpcProtocol.BackupPayload(repositoryId);
        var req = new ServiceIpcProtocol.Request("backup", JsonSerializer.Serialize(payload, JsonOptions));

        await writer.WriteLineAsync(JsonSerializer.Serialize(req, JsonOptions).AsMemory(), cancellationToken);

        var line = await ReadOperationResponseAsync(reader, cancellationToken)
            ?? throw new InvalidOperationException("Service closed connection unexpectedly during backup.");

        var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!resp.Success)
        {
            throw new InvalidOperationException(resp.ErrorMessage ?? "Service failed to run the backup.");
        }

        if (resp.PayloadJson is null)
        {
            throw new InvalidOperationException("Service returned empty backup payload.");
        }

        return JsonSerializer.Deserialize<ServiceIpcProtocol.BackupResponse>(resp.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to decode service backup response.");
    }

    public Task UpdateScheduleAsync(string repositoryId, ViewModels.SourceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(settings);

        var payload = new ServiceIpcProtocol.SchedulePreferencesPayload(
            repositoryId,
            settings.Enabled,
            (settings.BackupHour * 60) + settings.BackupMinute,
            settings.DrillEveryDays,
            settings.KeepDaily,
            settings.KeepWeekly,
            settings.KeepMonthly,
            settings.Prune);

        return SendAsync("updateSchedule", payload, "change the schedule", cancellationToken);
    }

    public Task RemoveScheduleAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        return SendAsync(
            "removeSchedule",
            new ServiceIpcProtocol.RemoveSchedulePayload(repositoryId),
            "stop protecting that source",
            cancellationToken);
    }

    /// <summary>
    /// One request whose answer is only whether it worked.
    /// </summary>
    /// <remarks>
    /// The commands that return something each parse their own reply; these two do not, and writing
    /// the connect-write-read sequence out twice more would be three chances to get the timeout or the
    /// error path subtly different from the others.
    /// </remarks>
    private async Task SendAsync<TPayload>(
        string command,
        TPayload payload,
        string description,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Service IPC is only supported on Windows.");
        }

        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cancellationToken);

        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var request = new ServiceIpcProtocol.Request(command, JsonSerializer.Serialize(payload, JsonOptions));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellationToken);

        var line = await ReadOperationResponseAsync(reader, cancellationToken)
            ?? throw new InvalidOperationException($"Service closed connection unexpectedly; it did not {description}.");

        var response = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!response.Success)
        {
            throw new InvalidOperationException(response.ErrorMessage ?? $"The service did not {description}.");
        }
    }

    private static async Task<string?> ReadOperationResponseAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromMinutes(30), cancellationToken);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                "The service has not returned a result after 30 minutes. The operation may still be running. " +
                "Do not repeat repository creation until its outcome has been checked. Refresh protection status or inspect the service logs.", error);
        }
    }

}