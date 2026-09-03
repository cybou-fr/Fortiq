using System.Diagnostics;
using System.Text;

namespace Fortiq.Infrastructure.Restic;

public enum ResticOperation
{
    Version,
    Initialize,
    Backup,
    Snapshots,
    Check,
    Restore
}

public sealed record ResticProcessRequest(
    ResticOperation Operation,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record ResticProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IResticProcessRunner
{
    Task<ResticProcessResult> RunAsync(
        VerifiedEngine engine,
        ResticProcessRequest request,
        CancellationToken cancellationToken);
}

public sealed class ResticProcessRunner : IResticProcessRunner
{
    public const int DefaultOutputLimit = 4 * 1024 * 1024;
    private static readonly HashSet<string> AllowedEnvironmentVariables =
        new(StringComparer.OrdinalIgnoreCase) { "LOCALAPPDATA", "TEMP", "TMP" };
    private readonly int _outputLimit;

    public ResticProcessRunner(int outputLimit = DefaultOutputLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLimit);
        _outputLimit = outputLimit;
    }

    public async Task<ResticProcessResult> RunAsync(
        VerifiedEngine engine,
        ResticProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);

        using var process = new Process { StartInfo = CreateStartInfo(engine, request) };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start repository engine.");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, _outputLimit, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, _outputLimit, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new ResticProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    internal static ProcessStartInfo CreateStartInfo(VerifiedEngine engine, ResticProcessRequest request)
    {
        if (!Path.IsPathFullyQualified(engine.AbsolutePath))
        {
            throw new ArgumentException("Verified engine path must be absolute.", nameof(engine));
        }

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("Engine working directory does not exist.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = engine.AbsolutePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        startInfo.Environment.Clear();
        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                if (!AllowedEnvironmentVariables.Contains(pair.Key))
                {
                    throw new ArgumentException($"Environment variable '{pair.Key}' is not allowed.", nameof(request));
                }

                startInfo.Environment.Add(pair.Key, pair.Value);
            }
        }

        startInfo.ArgumentList.Add(CommandFor(request.Operation));
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string CommandFor(ResticOperation operation) => operation switch
    {
        ResticOperation.Version => "version",
        ResticOperation.Initialize => "init",
        ResticOperation.Backup => "backup",
        ResticOperation.Snapshots => "snapshots",
        ResticOperation.Check => "check",
        ResticOperation.Restore => "restore",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        var exceeded = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                if (exceeded)
                {
                    throw new InvalidDataException("Engine output exceeded the configured limit.");
                }

                return result.ToString();
            }

            var remaining = limit - result.Length;
            if (read > remaining)
            {
                if (remaining > 0)
                {
                    result.Append(buffer, 0, remaining);
                }

                exceeded = true;
                continue;
            }

            result.Append(buffer, 0, read);
        }
    }
}
