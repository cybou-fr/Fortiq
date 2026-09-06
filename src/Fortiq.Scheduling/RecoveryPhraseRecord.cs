using System.Text.Json.Nodes;

namespace Fortiq.Scheduling;

/// <summary>
/// Whether anybody ever wrote down the recovery phrase for a repository.
/// </summary>
/// <remarks>
/// Three states, not two, and the third is the reason this is a type rather than a boolean.
///
/// <see cref="Unknown"/> is a repository provisioned before Fortiq recorded this. Its owner may well
/// have written the words on paper and put them in a drawer; nothing here knows either way, and
/// treating silence as failure would paint every existing installation red for something most of them
/// did correctly. A product that cries wolf teaches people to ignore the one time it is right.
///
/// <see cref="Issued"/> is the dangerous one, and it is a positive fact rather than an absence: Fortiq
/// generated a phrase, handed it to a screen, and never saw it confirmed. That happens when the
/// application is killed, or the machine loses power, while the words are being displayed - and the
/// schedule is already written by then, so the repository goes on backing up nightly and passing its
/// drills. Without this, such a repository is indistinguishable from a healthy one.
/// </remarks>
public enum RecoveryPhraseStatus
{
    /// <summary>Nothing was recorded, which is every repository older than this record.</summary>
    Unknown,

    /// <summary>A phrase was generated and shown, and nobody confirmed having written it down.</summary>
    Issued,

    /// <summary>Somebody typed the requested words back, so the phrase left the screen on paper.</summary>
    Confirmed
}

/// <summary>
/// Records, beside the schedules, whether a repository's recovery phrase was ever written down.
/// </summary>
/// <remarks>
/// Kept in the machine's state directory rather than in the recovery kit. The kit is the thing that
/// travels to another machine and whose integrity is checked; this is a fact about a person at a
/// keyboard on this machine, it has no business changing what the kit hashes to, and a kit copied to
/// a second machine must not carry an answer that was only ever true of the first.
/// </remarks>
public sealed class RecoveryPhraseRecord
{
    private const string Schema = "fortiq.recovery-phrase";
    private const int Version = 1;

    private readonly string _directory;

    public RecoveryPhraseRecord(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _directory = Path.Combine(Path.GetFullPath(stateDirectory), "phrases");
    }

    /// <summary>Records that a phrase was generated and shown, and not yet confirmed.</summary>
    public Task IssuedAsync(string scheduleId, DateTimeOffset at, CancellationToken cancellationToken) =>
        WriteAsync(scheduleId, confirmed: false, at, cancellationToken);

    /// <summary>
    /// Records that somebody typed the requested words back.
    /// </summary>
    /// <remarks>
    /// Written even when no issuance was recorded. A person who confirms has demonstrably seen the
    /// words, and that is worth keeping whatever the machine did or did not write beforehand.
    /// </remarks>
    public Task ConfirmedAsync(string scheduleId, DateTimeOffset at, CancellationToken cancellationToken) =>
        WriteAsync(scheduleId, confirmed: true, at, cancellationToken);

    /// <summary>What this machine knows about <paramref name="scheduleId"/>'s phrase.</summary>
    /// <remarks>
    /// A record that cannot be read is <see cref="RecoveryPhraseStatus.Unknown"/> rather than a
    /// failure. This decides whether to raise an alarm about somebody's backups; a damaged file is not
    /// evidence that they never wrote their words down, and health reporting must not stop because one
    /// small file on disk is corrupt.
    /// </remarks>
    public async Task<RecoveryPhraseStatus> ReadAsync(string scheduleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);

        try
        {
            var path = PathFor(scheduleId);
            if (!File.Exists(path))
            {
                return RecoveryPhraseStatus.Unknown;
            }

            var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            if (document?["schema"]?.GetValue<string>() != Schema)
            {
                return RecoveryPhraseStatus.Unknown;
            }

            return document["confirmed"]?.GetValue<bool>() == true
                ? RecoveryPhraseStatus.Confirmed
                : RecoveryPhraseStatus.Issued;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
        {
            return RecoveryPhraseStatus.Unknown;
        }
    }

    private async Task WriteAsync(string scheduleId, bool confirmed, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        Directory.CreateDirectory(_directory);

        var document = new JsonObject
        {
            ["schema"] = Schema,
            ["version"] = Version,
            ["scheduleId"] = scheduleId,
            ["confirmed"] = confirmed,
            ["at"] = at.ToString("O")
        };

        // Written whole and moved into place, like the other small state files: a record read halfway
        // through a write would answer this question with whatever half arrived.
        var path = PathFor(scheduleId);
        var temporary = path + ".partial";
        await File.WriteAllTextAsync(temporary, document.ToJsonString(), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(string scheduleId)
    {
        if (scheduleId.Length is 0 or > 128
            || !scheduleId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new InvalidDataException("A schedule ID must be letters, digits, '.', '_' or '-'.");
        }

        return Path.Combine(_directory, scheduleId + ".json");
    }
}
