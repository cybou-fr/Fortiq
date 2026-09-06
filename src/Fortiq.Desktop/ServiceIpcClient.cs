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
    Task ClearLockAsync(string repositoryId, CancellationToken cancellationToken = default);
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

        var line = await ReadOperationResponseAsync(reader, cancellationToken, writer)
            ?? throw new InvalidOperationException("Service closed connection unexpectedly during provisioning.");

        var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!resp.Success)
        {
            throw await ExplainAsync("provision", resp.ErrorMessage, "Service failed to provision repository.", cancellationToken);
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

        var line = await ReadOperationResponseAsync(reader, cancellationToken, writer)
            ?? throw new InvalidOperationException("Service closed connection unexpectedly during restore drill.");

        var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!resp.Success)
        {
            throw await ExplainAsync("prove", resp.ErrorMessage, "Service failed to execute restore drill.", cancellationToken);
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

        var line = await ReadOperationResponseAsync(reader, cancellationToken, writer)
            ?? throw new InvalidOperationException("Service closed connection unexpectedly during backup.");

        var resp = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!resp.Success)
        {
            throw await ExplainAsync("backup", resp.ErrorMessage, "Service failed to run the backup.", cancellationToken);
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

    public Task ClearLockAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        return SendAsync(
            "clearLock",
            new ServiceIpcProtocol.ClearLockPayload(repositoryId),
            "clear the lock on that repository",
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

        var line = await ReadOperationResponseAsync(reader, cancellationToken, writer)
            ?? throw new InvalidOperationException($"Service closed connection unexpectedly; it did not {description}.");

        var response = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions)
            ?? throw new InvalidOperationException("Malformed IPC response received from service.");

        if (!response.Success)
        {
            throw await ExplainAsync(command, response.ErrorMessage, $"The service did not {description}.", cancellationToken);
        }
    }

    /// <summary>
    /// Waits for the answer, and tells the service to stop if the caller stops waiting.
    /// </summary>
    /// <remarks>
    /// Abandoning the read on its own would leave the operation running to completion on the other
    /// side, invisibly - a cancel button that cancelled only the waiting. The service treats anything
    /// written while a request is outstanding as "stop", so a line goes down the pipe first; if the
    /// pipe is already gone, the service sees that instead, which means the same thing.
    /// </remarks>
    /// <summary>
    /// Turns a refusal into an exception carrying something a person can act on.
    /// </summary>
    /// <remarks>
    /// The judgement lives in <see cref="ServiceSkewMessage"/>, which is pure and tested; this is the
    /// part that needs the pipe. The service's version is asked for only once a request has already
    /// been refused as unknown, so the extra round trip is spent on a path that has already failed and
    /// buys a message naming both numbers instead of telling somebody to go and find them.
    /// </remarks>
    private async Task<Exception> ExplainAsync(string command, string? reported, string fallback, CancellationToken cancellationToken)
    {
        var serviceVersion = ServiceSkewMessage.IsVersionSkew(reported)
            ? await ServiceVersionAsync(cancellationToken)
            : null;

        return new InvalidOperationException(
            ServiceSkewMessage.Describe(command, reported, fallback, serviceVersion));
    }

    /// <summary>The version the service reports, or null when it will not say.</summary>
    private async Task<string?> ServiceVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var request = new ServiceIpcProtocol.Request("status");
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellationToken);

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<ServiceIpcProtocol.Response>(line, JsonOptions);
            if (response is not { Success: true, PayloadJson: { } payload })
            {
                return null;
            }

            var status = JsonSerializer.Deserialize<ServiceIpcProtocol.StatusResponse>(payload, JsonOptions);
            return string.IsNullOrWhiteSpace(status?.Version) ? null : status.Version;
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A service too old to answer this is exactly the service this is asking about, and the
            // message says so without the number rather than failing to appear.
            return null;
        }
    }

    private static async Task<string?> ReadOperationResponseAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        StreamWriter? writer = null)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromMinutes(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync("cancel");
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The connection has already gone, which the service reads as the same request.
                }
            }

            throw;
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                "The service has not returned a result after 30 minutes. The operation may still be running. " +
                "Do not repeat repository creation until its outcome has been checked. Refresh protection status or inspect the service logs.", error);
        }
    }

}