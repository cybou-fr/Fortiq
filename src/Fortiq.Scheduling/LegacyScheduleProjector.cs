using System.Globalization;
using Fortiq.Application;
using Fortiq.CommunityModel;
using Fortiq.Domain;

namespace Fortiq.Scheduling;

/// <summary>What is known about a schedule beyond the schedule file itself.</summary>
/// <param name="ScheduleId">Which schedule this describes.</param>
/// <param name="RecoveryPhraseConfirmed">
/// Whether the person has demonstrated they hold the recovery phrase. Without this the projection
/// would present an unconfirmed phrase as a recovery route, which is precisely the claim Fortiq
/// exists not to make.
/// </param>
/// <param name="DeviceKeyPresent">Whether a device-bound key exists for unattended writes.</param>
public sealed record ScheduleFacts(
    string ScheduleId,
    bool RecoveryPhraseConfirmed = false,
    bool DeviceKeyPresent = true);

/// <summary>
/// Reads today's schedules as tomorrow's resource model.
/// </summary>
/// <remarks>
/// A bridge, and openly a temporary one. Spec 30 asks for the Community read model to be populated
/// by projecting the current schedule files before anything writes them in the new shape, so that
/// the model can be looked at, drafted against and shown on screen without recreating a single
/// repository or re-encrypting a single byte.
///
/// It lives in the scheduling assembly rather than beside the model on purpose. The dependency runs
/// legacy to new and only that way; <c>Fortiq.CommunityModel</c> references nothing, so when native
/// task documents arrive there is no cycle to unpick and this file is simply deleted.
///
/// The projection is deliberately lossless about the awkward parts. One schedule carries a folder,
/// a destination, a kit directory, a recurrence, a drill recurrence, a retention recurrence and a
/// retention policy; every one of those becomes a resource that says the same thing, and where the
/// old model cannot answer a question the new one asks - which identity, which credential, whether
/// the storage is immutable - the answer is absent rather than invented.
/// </remarks>
public static class LegacyScheduleProjector
{
    /// <summary>The one engine Community writes, named so recovery kits stay format-aware.</summary>
    public const string EngineId = "engine-restic";

    public static ResourceCatalog Project(
        IReadOnlyList<BackupSchedule> schedules,
        IReadOnlyList<ScheduleFacts>? facts = null)
    {
        ArgumentNullException.ThrowIfNull(schedules);

        var sources = new List<Source>();
        var storages = new List<Storage>();
        var credentials = new List<StorageCredentialRef>();
        var identities = new List<Identity>();
        var keys = new List<IdentityKey>();
        var profiles = new List<EncryptionProfile>();
        var routes = new List<BackupRoute>();
        var tasks = new List<BackupTask>();

        foreach (var schedule in schedules)
        {
            var known = facts?.FirstOrDefault(item => string.Equals(item.ScheduleId, schedule.Id, StringComparison.Ordinal));

            // The same folder protected by two schedules becomes one Source. That is the first thing
            // this model buys: today those are two unrelated records that happen to hold equal
            // strings, and nothing can tell that they are the same folder.
            var sourceId = "source-" + Slug(schedule.SourceStableId, schedule.SourcePath);
            if (!sources.Any(source => source.Id == sourceId))
            {
                sources.Add(new Source(sourceId, FolderName(schedule.SourcePath), SourceKind.Folder, schedule.SourcePath));
            }

            var storage = ProjectStorage(schedule.RepositoryLocation);
            if (!storages.Any(existing => existing.Id == storage.Id))
            {
                storages.Add(storage);
            }

            if (storage.CredentialRef is { } credentialRef && !credentials.Any(existing => existing.Id == credentialRef))
            {
                credentials.Add(new StorageCredentialRef(
                    credentialRef,
                    $"Object storage login for {storage.Name}",
                    StorageCredentialKind.ObjectStorageKey));
            }

            // Two principals, because that is what a Fortiq repository actually has: a phrase
            // somebody wrote down, and a key sealed to this machine. They were never named as
            // principals before - the phrase was a field on a repository and the device key was an
            // implementation detail - which is why nothing could answer "who can recover this?".
            var scope = Slug(schedule.Id, schedule.Id);
            var phraseIdentityId = "identity-phrase-" + scope;
            var deviceIdentityId = "identity-this-pc";

            identities.Add(new Identity(phraseIdentityId, "Recovery phrase", IdentityKind.PaperRecovery));
            if (!identities.Any(identity => identity.Id == deviceIdentityId))
            {
                identities.Add(new Identity(deviceIdentityId, "This PC", IdentityKind.Device));
            }

            var phraseKeyId = "key-phrase-" + scope;
            keys.Add(new IdentityKey(
                phraseKeyId,
                phraseIdentityId,
                IdentityKeyKind.RecoveryPhrase,
                $"24 words, issued with the recovery kit in {schedule.KitDirectory}",
                Confirmed: known?.RecoveryPhraseConfirmed ?? false));

            var deviceKeyId = "key-device-" + scope;
            var writers = new List<string>();
            if (known?.DeviceKeyPresent ?? true)
            {
                keys.Add(new IdentityKey(
                    deviceKeyId,
                    deviceIdentityId,
                    IdentityKeyKind.DeviceBound,
                    "Sealed to this machine, so unattended backups can write without anybody present",
                    // A device key is proved by existing: the machine either can unlock or cannot.
                    Confirmed: true));
                writers.Add(deviceKeyId);
            }

            var profileId = "enc-" + scope;
            profiles.Add(new EncryptionProfile(profileId, "Personal recovery", [phraseKeyId], writers));

            var routeId = "route-" + scope;
            routes.Add(new BackupRoute(
                routeId,
                storage.Id,
                EngineId,
                profileId,
                Retention: ProjectRetention(schedule),
                RetentionTrigger: ProjectTrigger(schedule.RetentionRecurrence),
                DrillTrigger: ProjectTrigger(schedule.DrillRecurrence)));

            tasks.Add(new BackupTask(
                "task-" + scope,
                FolderName(schedule.SourcePath),
                [sourceId],
                ProjectTrigger(schedule.Recurrence) ?? new ManualTrigger(),
                [routeId],
                schedule.Enabled,
                schedule.CatchUp == CatchUp.Skip ? CatchUpPolicy.Skip : CatchUpPolicy.Once));
        }

        return new ResourceCatalog(
            sources,
            storages,
            credentials,
            storages.Count == 0 ? [] : [new RepositoryEngineRef(EngineId, "restic", "0.19.1")],
            identities,
            keys,
            profiles,
            routes,
            tasks);
    }

    /// <summary>
    /// Turns a repository location into a place, and says only what the string proves.
    /// </summary>
    /// <remarks>
    /// Capabilities are the interesting part. An <c>s3:https://…</c> location is remote and, when
    /// the URL is HTTPS, encrypted in transit - both readable from the string. Whether the bucket is
    /// versioned, immutable or has object lock is not readable from anywhere without asking it, so
    /// none of those is claimed. Nothing here probes; a projector that guessed would put a
    /// ransomware claim on screen that nobody had checked.
    /// </remarks>
    private static Storage ProjectStorage(string location)
    {
        var id = "storage-" + Slug(location, location);

        if (RepositoryLocation.KindOf(location) == RepositoryLocationKind.ObjectStorage)
        {
            var url = location["s3:".Length..];
            var secure = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return new Storage(
                id,
                ObjectStorageName(url),
                StorageBackend.S3,
                location,
                CredentialRef: "cred-" + Slug(url, url),
                Capabilities: StorageCapabilities.Remote
                    | StorageCapabilities.IndependentOfEndpoint
                    | (secure ? StorageCapabilities.EncryptedTransport : StorageCapabilities.None));
        }

        return new Storage(id, location, StorageBackend.FileSystem, location);
    }

    private static RetentionRule? ProjectRetention(BackupSchedule schedule) =>
        schedule.Retention is { } policy && policy.KeepsSomething
            ? new RetentionRule(
                policy.KeepLast,
                policy.KeepDaily,
                policy.KeepWeekly,
                policy.KeepMonthly,
                policy.KeepYearly,
                policy.KeepWithin)
            : null;

    /// <summary>
    /// A recurrence as a trigger, keeping the time zone by name.
    /// </summary>
    /// <remarks>
    /// By name, not by object: a trigger is a declaration meant to be written to a file and read on
    /// another machine or in another year, and a <c>TimeZoneInfo</c> is this process's idea of what
    /// that name currently means. The scheduler keeps doing the arithmetic; nothing here computes an
    /// occurrence.
    /// </remarks>
    private static Trigger? ProjectTrigger(Recurrence? recurrence) => recurrence switch
    {
        null => null,
        EveryInterval interval => new IntervalTrigger(interval.Period),
        DailyAt daily => new DailyTrigger(daily.TimeOfDay, daily.TimeZone.Id, daily.Days),
        _ => new ManualTrigger()
    };

    /// <summary>The folder's own name, with enough of its parent to tell two of them apart.</summary>
    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
        {
            return trimmed.Length > 0 ? trimmed : path;
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
        return string.IsNullOrEmpty(parent) ? name : Path.Combine(parent, name);
    }

    private static string ObjectStorageName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.Host + parsed.AbsolutePath.TrimEnd('/')
            : url;

    /// <summary>
    /// A stable, readable identifier for a value that was never meant to be one.
    /// </summary>
    /// <remarks>
    /// Identifiers derived from paths and URLs have to survive being written to a file and compared
    /// later, so this is deterministic and case-insensitive - two schedules naming the same folder
    /// in different cases are the same folder on Windows, and must project to one Source. Anything
    /// that is not a letter or a digit becomes a hyphen, and a short hash of the original is
    /// appended so that two different values cannot collapse into one identifier.
    /// </remarks>
    private static string Slug(string preferred, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        var readable = new string(value
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');

        while (readable.Contains("--", StringComparison.Ordinal))
        {
            readable = readable.Replace("--", "-", StringComparison.Ordinal);
        }

        if (readable.Length > 40)
        {
            readable = readable[^40..].TrimStart('-');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        var suffix = Convert.ToHexStringLower(hash)[..8];

        return readable.Length == 0
            ? suffix
            : string.Create(CultureInfo.InvariantCulture, $"{readable}-{suffix}");
    }
}
