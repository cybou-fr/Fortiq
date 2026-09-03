using System.Text.Json;
using Fortiq.Application;

namespace Fortiq.Recover;

public enum RecoveryOperation { Inspect, Snapshots, Check, Restore }

public sealed record RecoveryCommand(
    RecoveryOperation Operation,
    string Repository,
    string EngineRoot,
    string? Envelope,
    string? SnapshotId,
    string? Target,
    string? Source)
{
    /// <summary>Inspect describes a recovery kit; every other operation has to unlock it first.</summary>
    public bool RequiresUnlock => Operation != RecoveryOperation.Inspect;
}

public interface IRecoveryCommandExecutor
{
    Task<object> ExecuteAsync(RecoveryCommand command, IRecoveryMaterialReader material, CancellationToken token);
}

/// <summary>
/// Supplies the recovery mnemonic. It is never read from the command line: process arguments are
/// visible to other processes and end up in shell history and logs.
/// </summary>
public interface IRecoveryMaterialReader
{
    Task<string> ReadMnemonicAsync(CancellationToken token);
}

public static class RecoveryCli
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 64;
    public const int ExitDataError = 69;
    public const int ExitUnlockFailed = 77;

    private static readonly JsonSerializerOptions OutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string[] KnownOptions =
        ["--repository", "--engine-root", "--envelope", "--snapshot", "--target", "--source"];

    public static async Task<int> RunAsync(
        string[] args,
        IRecoveryCommandExecutor executor,
        IRecoveryMaterialReader material,
        TextWriter output,
        TextWriter error,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(executor);
        try
        {
            var result = await executor.ExecuteAsync(Parse(args), material, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(result, OutputJson));
            return ExitSuccess;
        }
        catch (ArgumentException failure)
        {
            await error.WriteLineAsync(failure.Message);
            return ExitUsage;
        }
        catch (UnlockFailedException)
        {
            // One unified failure: a caller cannot tell a wrong mnemonic from a missing key, and no
            // repository or snapshot detail leaks through the exit path.
            await error.WriteLineAsync("UnlockFailed");
            return ExitUnlockFailed;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or FormatException or RestoreRejectedException)
        {
            await error.WriteLineAsync(failure.Message);
            return ExitDataError;
        }
    }

    public static RecoveryCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || !Enum.TryParse<RecoveryOperation>(args[0], true, out var operation))
        {
            throw new ArgumentException("Expected inspect, snapshots, check, or restore.", nameof(args));
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Every option requires a value.", nameof(args));
            }

            var name = args[index];
            if (!KnownOptions.Contains(name, StringComparer.Ordinal) || !options.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"Unknown or duplicate option: {name}.", nameof(args));
            }
        }

        var repository = RequiredPath(options, "--repository");
        var engineRoot = RequiredPath(options, "--engine-root");
        options.TryGetValue("--envelope", out var envelope);
        options.TryGetValue("--snapshot", out var snapshot);
        options.TryGetValue("--target", out var target);
        options.TryGetValue("--source", out var source);

        if (operation == RecoveryOperation.Restore && (string.IsNullOrWhiteSpace(snapshot) || string.IsNullOrWhiteSpace(target)))
        {
            throw new ArgumentException("Restore requires --snapshot and --target.", nameof(args));
        }

        if (operation != RecoveryOperation.Restore && (snapshot is not null || target is not null || source is not null))
        {
            throw new ArgumentException("Snapshot, target and source are restore-only.", nameof(args));
        }

        if (operation != RecoveryOperation.Inspect && string.IsNullOrWhiteSpace(envelope))
        {
            throw new ArgumentException($"{operation} requires --envelope.", nameof(args));
        }

        return new RecoveryCommand(
            operation,
            repository,
            engineRoot,
            envelope is null ? null : Path.GetFullPath(envelope),
            snapshot,
            target is null ? null : Path.GetFullPath(target),
            source);
    }

    private static string RequiredPath(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing {name}.", nameof(options));
}
