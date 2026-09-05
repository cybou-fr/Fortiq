using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fortiq.Infrastructure.Updates;

/// <summary>How far an update had got when the machine last stopped looking at it.</summary>
public enum UpdateIntentState
{
    /// <summary>Files are being written into staging. Nothing installed has been touched.</summary>
    Staging,

    /// <summary>Installed files are being replaced. Some may be missing right now.</summary>
    Swapping,

    /// <summary>Every file was replaced and the originals are no longer needed.</summary>
    Committed
}

/// <summary>
/// The record an update writes before it touches anything installed, so that an interrupted update is
/// recognisable rather than merely broken.
/// </summary>
/// <remarks>
/// Modelled on <c>provisioning-intent.json</c>, for the same reason: the dangerous moment is not the
/// failure, it is the next start-up, when a half-updated directory looks exactly like a working one.
/// A machine that crashes mid-swap has some new binaries and some old ones, which is the mix-and-match
/// state the metadata checks went to such lengths to prevent - arrived at by accident instead.
/// </remarks>
internal sealed class UpdateIntent
{
    internal const string FileName = "update-intent.json";

    private const string Schema = "fortiq.update-intent";
    private const int Version = 1;

    // The state is written as a name rather than an ordinal, matching provisioning-intent.json. This
    // file is read by whoever is working out what a machine was doing when it stopped, and "swapping"
    // tells them that; "1" makes them go and find the enum.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    private UpdateIntent(string path, IntentDocument document)
    {
        _path = path;
        Document = document;
    }

    internal IntentDocument Document { get; private set; }

    internal static async Task<UpdateIntent> BeginAsync(
        string workingDirectory,
        string installDirectory,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var path = Path.Combine(Path.GetFullPath(workingDirectory), FileName);

        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                "This directory holds an unfinished update. Recover it before starting another, " +
                "or the recovery will not know which of the two it is undoing.");
        }

        var document = new IntentDocument(
            Schema,
            Version,
            installDirectory,
            relativePaths,
            DateTimeOffset.UtcNow,
            UpdateIntentState.Staging);

        var intent = new UpdateIntent(path, document);
        await intent.WriteAsync(cancellationToken);
        return intent;
    }

    internal static async Task<UpdateIntent?> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(workingDirectory), FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<IntentDocument>(
            await File.ReadAllTextAsync(path, cancellationToken),
            Options) ?? throw new InvalidDataException("The update intent is empty.");

        if (document.Schema != Schema || document.Version != Version)
        {
            throw new InvalidDataException("Unsupported update intent schema or version.");
        }

        return new UpdateIntent(path, document);
    }

    internal async Task AdvanceAsync(UpdateIntentState state, CancellationToken cancellationToken)
    {
        Document = Document with { State = state };
        await WriteAsync(cancellationToken);
    }

    internal void Delete() => File.Delete(_path);

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        // Written to a sibling and moved into place, so that a crash during the write leaves the
        // previous state readable rather than a truncated file that recovery cannot parse. An update
        // whose intent cannot be read is one nobody can safely undo.
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(Document, Options), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    internal sealed record IntentDocument(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("installDirectory")] string InstallDirectory,
        [property: JsonPropertyName("relativePaths")] IReadOnlyList<string> RelativePaths,
        [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("state")] UpdateIntentState State);
}
