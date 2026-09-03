using System.Text.Json;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Recover;

public enum RecoveryOperation { Inspect, Snapshots, Check, Restore }
public sealed record RecoveryCommand(RecoveryOperation Operation, string Repository, string EngineRoot, string? SnapshotId, string? Target);
public interface IRecoveryCommandExecutor { Task<object> ExecuteAsync(RecoveryCommand command, CancellationToken token); }

public static class RecoveryCli
{
    private static readonly JsonSerializerOptions OutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task<int> RunAsync(string[] args, IRecoveryCommandExecutor executor, TextWriter output, TextWriter error, CancellationToken token)
    {
        try { await output.WriteLineAsync(JsonSerializer.Serialize(await executor.ExecuteAsync(Parse(args), token), OutputJson)); return 0; }
        catch (ArgumentException e) { await error.WriteLineAsync(e.Message); return 64; }
        catch (Exception e) when (e is IOException or InvalidDataException) { await error.WriteLineAsync(e.Message); return 69; }
    }

    public static RecoveryCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !Enum.TryParse<RecoveryOperation>(args[0], true, out var operation)) throw new ArgumentException("Expected inspect, snapshots, check, or restore.", nameof(args));
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count || !args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Every option requires a value.", nameof(args));
            var name = args[i];
            if (name is not ("--repository" or "--engine-root" or "--snapshot" or "--target") || !options.TryAdd(name, args[i + 1])) throw new ArgumentException($"Unknown or duplicate option: {name}.", nameof(args));
        }
        var repository = RequiredPath(options, "--repository"); var engineRoot = RequiredPath(options, "--engine-root");
        options.TryGetValue("--snapshot", out var snapshot); options.TryGetValue("--target", out var target);
        if (operation == RecoveryOperation.Restore && (string.IsNullOrWhiteSpace(snapshot) || string.IsNullOrWhiteSpace(target))) throw new ArgumentException("Restore requires --snapshot and --target.", nameof(args));
        if (operation != RecoveryOperation.Restore && (snapshot is not null || target is not null)) throw new ArgumentException("Snapshot and target are restore-only.", nameof(args));
        return new(operation, repository, engineRoot, snapshot, target is null ? null : Path.GetFullPath(target));
    }

    private static string RequiredPath(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? Path.GetFullPath(value) : throw new ArgumentException($"Missing {name}.", nameof(options));
}

public sealed class RecoveryCommandExecutor : IRecoveryCommandExecutor
{
    public async Task<object> ExecuteAsync(RecoveryCommand command, CancellationToken token)
    {
        if (command.Operation != RecoveryOperation.Inspect) throw new InvalidDataException("Unlock provider is not connected; command unavailable.");
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(command.EngineRoot, "manifest.json"), token);
        var entry = manifest.Engines.SingleOrDefault(x => x.Name == "restic" && x.Rid == "win-x64") ?? throw new InvalidDataException("Pinned restic entry missing.");
        var engine = await EngineBinaryVerifier.VerifyAsync(command.EngineRoot, entry, token);
        return new { schema = "fortiq.recovery-inspect", version = 1, repository = command.Repository, repositoryPresent = File.Exists(Path.Combine(command.Repository, "config")), engine = new { engine.Name, engine.Version, engine.Rid, engine.Sha256 }, unlockRequired = true };
    }
}
