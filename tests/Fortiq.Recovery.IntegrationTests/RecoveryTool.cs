using System.Diagnostics;

namespace Fortiq.Recovery.IntegrationTests;

public sealed record RecoveryToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs the published recovery tool as a separate process, the way a real recovery does. The
/// mnemonic goes in on standard input; it is never passed as an argument.
/// </summary>
public static class RecoveryTool
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fortiq.Recover.exe");

    public static async Task<RecoveryToolResult> RunAsync(
        string[] arguments,
        string? mnemonic,
        Fortiq.Application.ObjectStorageCredentials? storage = null)
    {
        Skip.IfNot(File.Exists(Path), "The recovery tool was not built next to the tests.");

        var startInfo = new ProcessStartInfo
        {
            FileName = Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (storage is not null)
        {
            startInfo.Environment["AWS_ACCESS_KEY_ID"] = storage.AccessKeyId;
            startInfo.Environment["AWS_SECRET_ACCESS_KEY"] = storage.SecretAccessKey;
            if (storage.Region is { Length: > 0 } region)
            {
                startInfo.Environment["AWS_DEFAULT_REGION"] = region;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the recovery tool.");
        if (mnemonic is not null)
        {
            await process.StandardInput.WriteLineAsync(mnemonic);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new RecoveryToolResult(process.ExitCode, await stdout, await stderr);
    }
}
