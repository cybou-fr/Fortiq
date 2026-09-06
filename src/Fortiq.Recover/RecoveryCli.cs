using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Domain;

namespace Fortiq.Recover;

public enum RecoveryOperation { Inspect, Snapshots, Files, Check, Restore }

public sealed record RecoveryCommand(
    RecoveryOperation Operation,
    string Repository,
    string EngineRoot,
    string? Kit,
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
    public const int ExitKitMismatch = 78;
    public const int ExitRepositoryBusy = 75;

    private static readonly JsonSerializerOptions OutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string[] KnownOptions =
        ["--repository", "--engine-root", "--kit", "--snapshot", "--target", "--source"];

    public static async Task<int> RunAsync(
        string[] args,
        IRecoveryCommandExecutor executor,
        IRecoveryMaterialReader material,
        TextWriter output,
        TextWriter error,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(executor);

        // Somebody reaching for this tool has usually lost something, is on a machine that is not
        // theirs, and will type --help before anything else. Answering that with "Expected inspect,
        // snapshots, files, check, or restore." and exit 64 is a poor first impression from the one
        // program in this product that has to work when everything else is gone.
        if (args is null || args.Length == 0)
        {
            // Run with nothing: still show the whole thing, but the exit code says it was not a
            // command, so a script that calls this by mistake does not read it as success.
            await error.WriteLineAsync(Usage);
            return ExitUsage;
        }

        if (args.Any(IsHelpRequest))
        {
            await output.WriteLineAsync(Usage);
            return ExitSuccess;
        }

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
        catch (RepositoryBusyException failure)
        {
            // Temporary by nature: the repository is in use, and the caller may simply try later.
            await error.WriteLineAsync(failure.Message);
            return ExitRepositoryBusy;
        }
        catch (RecoveryKitMismatchException failure)
        {
            // Distinct from a failed unlock: the kit and the target disagree about what they are.
            await error.WriteLineAsync(failure.Message);
            return ExitKitMismatch;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or FormatException or RestoreRejectedException)
        {
            await error.WriteLineAsync(failure.Message);
            return ExitDataError;
        }
    }

    private static bool IsHelpRequest(string argument) =>
        argument is "--help" or "-h" or "-?" or "/?" or "help";

    /// <summary>What to print when somebody asks what this is.</summary>
    public const string Usage = """
        Fortiq.Recover - open Fortiq backups on a machine that has no Fortiq.

        Usage:
          Fortiq.Recover.exe <command> --repository <path-or-s3-url> --kit <kit-folder> [options]

        Commands:
          inspect     Ask the repository what it is, and check it against the kit. Changes nothing.
          snapshots   List the backups the repository holds, with the date each was taken.
          files       List the files inside one backup.        Needs --snapshot
          check       Verify the repository's own integrity.
          restore     Write files back out.                    Needs --snapshot and an empty --target

        Options:
          --repository  Where the backups are: a folder, a drive, or an s3: URL.
          --kit         The recovery kit folder. It holds a file named kit.json.
          --snapshot    Which backup, by the id that 'snapshots' printed.
          --target      An empty folder to restore into. Existing files are never overwritten.
          --source      Restore only this path from the backup, rather than all of it.
          --engine-root Where the backup engine lives, if it is not beside this program.

        Your 24 recovery words are asked for at the keyboard, never on the command line: a command
        line is kept in shell history and is visible to anyone who can list running processes.

        For storage in S3, set AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_DEFAULT_REGION first.

        RECOVERY-GUIDE.md, in the folder above this one, walks through the whole thing.
        """;

    public static RecoveryCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || !Enum.TryParse<RecoveryOperation>(args[0], true, out var operation))
        {
            throw new ArgumentException("Expected inspect, snapshots, files, check, or restore.", nameof(args));
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

        var repository = RequiredRepository(options, "--repository");
        var engineRoot = RequiredPath(options, "--engine-root");
        options.TryGetValue("--kit", out var kit);
        options.TryGetValue("--snapshot", out var snapshot);
        options.TryGetValue("--target", out var target);
        options.TryGetValue("--source", out var source);

        if (operation == RecoveryOperation.Restore && (string.IsNullOrWhiteSpace(snapshot) || string.IsNullOrWhiteSpace(target)))
        {
            throw new ArgumentException("Restore requires --snapshot and --target.", nameof(args));
        }

        if (operation == RecoveryOperation.Files && string.IsNullOrWhiteSpace(snapshot))
        {
            throw new ArgumentException("Files requires --snapshot.", nameof(args));
        }

        if (operation != RecoveryOperation.Restore && operation != RecoveryOperation.Files && (snapshot is not null || target is not null || source is not null))
        {
            throw new ArgumentException("Snapshot, target and source are restore-only.", nameof(args));
        }

        if (operation != RecoveryOperation.Inspect && string.IsNullOrWhiteSpace(kit))
        {
            throw new ArgumentException($"{operation} requires --kit.", nameof(args));
        }

        return new RecoveryCommand(
            operation,
            repository,
            engineRoot,
            kit is null ? null : Path.GetFullPath(kit),
            snapshot,
            target is null ? null : Path.GetFullPath(target),
            source);
    }

    private static string RequiredPath(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing {name}.", nameof(options));

    private static string RequiredRepository(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? RepositoryLocation.Normalize(value)
            : throw new ArgumentException($"Missing {name}.", nameof(options));
}
