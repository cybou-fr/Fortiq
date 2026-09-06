using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fortiq.Desktop.ViewModels;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop;

/// <summary>A short-lived administrator client for one settings mutation.</summary>
public static class SourceSettingsElevation
{
    public const string Verb = "--source-operation";
    public sealed record Request(string Command, string RepositoryId, SourceSettings? Settings);

    public static async Task<bool> RunIfNeededAsync(string command, string repositoryId,
        SourceSettings? settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || WindowsPrivilegeChecker.IsElevated()
            || FortiqOperatorsGroup.IsCurrentUserMember()) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Request(command, repositoryId, settings)));
        try
        {
            using var worker = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe"))
            {
                UseShellExecute = true, Verb = "runas", ArgumentList = { Verb, encoded }
            }) ?? throw new InvalidOperationException("Windows did not start the settings operation.");
            // Do not report cancellation while the privileged mutation continues in the background.
            await worker.WaitForExitAsync(CancellationToken.None);
            if (worker.ExitCode != 0)
                throw new InvalidOperationException("The settings operation failed. Check that the Fortiq service is running and try again.");
            return true;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator approval was cancelled. Your settings have not been saved.", error);
        }
    }

    public static async Task<int> RunWorkerAsync(string encoded, IServiceIpcClient client)
    {
        try
        {
            if (encoded.Length > 16000) return 1;
            var request = JsonSerializer.Deserialize<Request>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            if (request is null || string.IsNullOrWhiteSpace(request.RepositoryId)) return 1;
            switch (request.Command)
            {
                case "updateSchedule" when request.Settings is not null:
                    await client.UpdateScheduleAsync(request.RepositoryId, request.Settings);
                    break;
                case "removeSchedule":
                    await client.RemoveScheduleAsync(request.RepositoryId);
                    break;
                case "clearLock":
                    await client.ClearLockAsync(request.RepositoryId);
                    break;
                default: return 1;
            }
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return 1;
        }
    }
}
