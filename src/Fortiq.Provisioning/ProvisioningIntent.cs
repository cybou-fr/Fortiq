using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fortiq.Provisioning;

/// <summary>
/// The record a provisioning run writes before it creates anything, and removes once the kit has
/// been proven to open the repository. Its only purpose is to make an interrupted run recognisable:
/// a repository whose kit was never finished must not be mistaken for a working one.
/// </summary>
internal sealed class ProvisioningIntent
{
    private const string FileName = "provisioning-intent.json";

    private readonly string _path;

    private ProvisioningIntent(string path, string repositoryPath, string kitPath, ProvisioningIntentState state)
    {
        _path = path;
        RepositoryPath = repositoryPath;
        KitPath = kitPath;
        State = state;
    }

    internal string RepositoryPath { get; }

    internal string KitPath { get; }

    internal ProvisioningIntentState State { get; private set; }

    internal static async Task<ProvisioningIntent> BeginAsync(
        string workingDirectory,
        string repositoryPath,
        string kitPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var path = Path.Combine(Path.GetFullPath(workingDirectory), FileName);
        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                "This working directory holds an unfinished provisioning run; clean it up before starting another.");
        }

        var document = new IntentDocument(
            Schema,
            Version,
            repositoryPath,
            kitPath,
            DateTimeOffset.UtcNow,
            ProvisioningIntentState.InProgress);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, Options), cancellationToken);
        return new ProvisioningIntent(path, repositoryPath, kitPath, ProvisioningIntentState.InProgress);
    }

    internal static async Task<ProvisioningIntent?> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(workingDirectory), FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<IntentDocument>(
            await File.ReadAllTextAsync(path, cancellationToken),
            Options) ?? throw new InvalidDataException("The provisioning intent is empty.");

        if (document.Schema != Schema || document.Version != Version)
        {
            throw new InvalidDataException("Unsupported provisioning intent schema or version.");
        }

        return new ProvisioningIntent(
            path,
            document.RepositoryPath,
            document.KitPath,
            document.State ?? ProvisioningIntentState.InProgress);
    }

    internal async Task MarkRemoteCleanupRequiredAsync(CancellationToken cancellationToken)
    {
        var existing = JsonSerializer.Deserialize<IntentDocument>(
            await File.ReadAllTextAsync(_path, cancellationToken),
            Options) ?? throw new InvalidDataException("The provisioning intent is empty.");
        var updated = existing with { State = ProvisioningIntentState.RemoteCleanupRequired };
        var temporary = _path + ".partial";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, Options), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
        State = ProvisioningIntentState.RemoteCleanupRequired;
    }

    internal Task CompleteAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        File.Delete(_path);
        return Task.CompletedTask;
    }

    internal static void Remove(string workingDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(workingDirectory), FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Removes what a failed run created. Both directories were empty or absent when the run began,
    /// so emptying them undoes exactly this run's work and nothing else.
    /// </summary>
    internal static void RollBack(string? repositoryPath, string kitPath)
    {
        Clear(kitPath);
        if (repositoryPath is not null)
        {
            Clear(repositoryPath);
        }
    }

    private static void Clear(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        // A directory that cannot be emptied is left as it is: reporting the original failure matters
        // more than hiding it behind a cleanup error.
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const string Schema = "fortiq.provisioning-intent";
    private const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed record IntentDocument(
        string Schema,
        int Version,
        string RepositoryPath,
        string KitPath,
        DateTimeOffset StartedAt,
        ProvisioningIntentState? State = null);
}

internal enum ProvisioningIntentState
{
    InProgress,
    RemoteCleanupRequired
}
