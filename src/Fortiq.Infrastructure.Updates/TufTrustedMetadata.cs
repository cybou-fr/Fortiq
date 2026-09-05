using System.Text.Json;

namespace Fortiq.Infrastructure.Updates;

/// <summary>
/// The client's trusted view of an update repository, advanced one role document at a time.
/// </summary>
/// <remarks>
/// The order is the point. Timestamp is fetched first and is the only document that must be fresh on
/// every check; it names the snapshot, snapshot names the targets, and targets names the binaries.
/// Each step checks the next document against something already trusted, so a server that serves an
/// old-but-validly-signed document cannot make the client act on it:
///
/// - <b>Rollback</b> is refused because every role's version must not go backwards, and because the
///   version a document declares must equal the version the document above it recorded for it.
/// - <b>Freeze</b> is refused because expiry is checked against the caller's clock at every step, so a
///   server that simply stops serving new metadata runs out of validity rather than pinning the client
///   at a vulnerable release forever.
/// - <b>Mix-and-match</b> is refused because snapshot fixes the version of targets, so components from
///   two different releases cannot both be presented as current.
///
/// The clock is passed in rather than read from <see cref="DateTimeOffset.UtcNow"/>, because expiry is
/// the one check whose failure mode is a machine being wrong rather than a server lying, and a test
/// that cannot move the clock cannot cover it.
/// </remarks>
public sealed class TufTrustedMetadata
{
    private const string SnapshotFileName = "snapshot.json";
    private const string TargetsFileName = "targets.json";

    private TufRoot _root;
    private SignedMetadata? _timestamp;
    private SignedMetadata? _snapshot;
    private SignedMetadata? _targets;

    private TufTrustedMetadata(TufRoot root)
    {
        _root = root;
    }

    /// <summary>The version of each role currently trusted, for recording in an update receipt.</summary>
    public long RootVersion => _root.Version;

    public long? TimestampVersion => _timestamp?.Version;

    public long? SnapshotVersion => _snapshot?.Version;

    public long? TargetsVersion => _targets?.Version;

    /// <summary>
    /// Begins from a root document that is trusted because it was shipped with the client, not because
    /// it was downloaded.
    /// </summary>
    /// <remarks>
    /// Its signatures are still checked, against the keys it itself names. That sounds circular and is
    /// not the point: it catches a root file corrupted or truncated on disk before the client starts
    /// trusting role definitions read out of a damaged document.
    /// </remarks>
    public static TufTrustedMetadata LoadTrustedRoot(ReadOnlySpan<byte> rootDocument)
    {
        var metadata = SignedMetadata.Parse(rootDocument);
        var root = TufRoot.Read(metadata);
        root.RequireSignatures(metadata, TufRole.Root);
        return new TufTrustedMetadata(root);
    }

    /// <summary>
    /// Rotates to a newer root document, which must be signed by both the root in force and the root
    /// taking over.
    /// </summary>
    /// <remarks>
    /// Both signatures, because either alone is a way to lose the repository. Verifying only against
    /// the old root lets the current holders hand trust to keys that cannot actually sign, and the next
    /// update is unverifiable. Verifying only against the new root lets anybody who can serve a file
    /// replace the root outright.
    /// </remarks>
    public void UpdateRoot(ReadOnlySpan<byte> rootDocument, DateTimeOffset now)
    {
        var metadata = SignedMetadata.Parse(rootDocument);
        var candidate = TufRoot.Read(metadata);

        if (candidate.Version <= _root.Version)
        {
            throw new TufMetadataException(
                $"The offered root is version {candidate.Version}; version {_root.Version} is already trusted.");
        }

        // One step at a time. Jumping from version 2 to version 5 would skip the key rotations recorded
        // in 3 and 4, and a key revoked in 3 would still be trusted for the jump that skipped it.
        if (candidate.Version != _root.Version + 1)
        {
            throw new TufMetadataException(
                $"The offered root is version {candidate.Version}; version {_root.Version + 1} comes next. " +
                "Root versions are applied one at a time so that no key rotation is skipped.");
        }

        _root.RequireSignatures(metadata, TufRole.Root);
        candidate.RequireSignatures(metadata, TufRole.Root);
        RequireUnexpired(candidate.Expires, TufRole.Root, now);

        _root = candidate;

        // Everything below root was validated against role definitions that no longer apply. Keeping it
        // would leave the client trusting a targets document signed by a key this rotation just revoked.
        _timestamp = null;
        _snapshot = null;
        _targets = null;
    }

    /// <summary>Accepts a newer <c>timestamp</c> document.</summary>
    public void UpdateTimestamp(ReadOnlySpan<byte> timestampDocument, DateTimeOffset now)
    {
        var metadata = Accept(timestampDocument, TufRole.Timestamp, now);

        if (_timestamp is { } trusted && metadata.Version < trusted.Version)
        {
            throw new TufMetadataException(
                $"The offered timestamp is version {metadata.Version}; version {trusted.Version} is already trusted.");
        }

        // Reading the snapshot entry now, so that a timestamp which does not describe a snapshot is
        // rejected as malformed rather than accepted and found useless one step later.
        _ = ReadMetaEntry(metadata, SnapshotFileName, TufRole.Timestamp);

        _timestamp = metadata;
        _snapshot = null;
        _targets = null;
    }

    /// <summary>Accepts the <c>snapshot</c> document the trusted timestamp names.</summary>
    public void UpdateSnapshot(ReadOnlySpan<byte> snapshotDocument, DateTimeOffset now)
    {
        var timestamp = _timestamp
            ?? throw new InvalidOperationException("A trusted timestamp is required before a snapshot is accepted.");

        var expected = ReadMetaEntry(timestamp, SnapshotFileName, TufRole.Timestamp);
        expected.Info?.RequireMatch(snapshotDocument, SnapshotFileName);

        var metadata = Accept(snapshotDocument, TufRole.Snapshot, now);

        if (metadata.Version != expected.Version)
        {
            throw new TufMetadataException(
                $"The offered snapshot is version {metadata.Version}; the trusted timestamp names version {expected.Version}.");
        }

        if (_snapshot is { } trusted && metadata.Version < trusted.Version)
        {
            throw new TufMetadataException(
                $"The offered snapshot is version {metadata.Version}; version {trusted.Version} is already trusted.");
        }

        _ = ReadMetaEntry(metadata, TargetsFileName, TufRole.Snapshot);

        _snapshot = metadata;
        _targets = null;
    }

    /// <summary>Accepts the <c>targets</c> document the trusted snapshot names.</summary>
    public void UpdateTargets(ReadOnlySpan<byte> targetsDocument, DateTimeOffset now)
    {
        var snapshot = _snapshot
            ?? throw new InvalidOperationException("A trusted snapshot is required before targets are accepted.");

        var expected = ReadMetaEntry(snapshot, TargetsFileName, TufRole.Snapshot);
        expected.Info?.RequireMatch(targetsDocument, TargetsFileName);

        var metadata = Accept(targetsDocument, TufRole.Targets, now);

        if (metadata.Version != expected.Version)
        {
            throw new TufMetadataException(
                $"The offered targets document is version {metadata.Version}; " +
                $"the trusted snapshot names version {expected.Version}.");
        }

        _targets = metadata;
    }

    /// <summary>
    /// What the trusted targets document says <paramref name="targetPath"/> must be, or null when it
    /// names no such target.
    /// </summary>
    public TufFileInfo? FindTarget(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var targets = _targets
            ?? throw new InvalidOperationException("A trusted targets document is required before a target is looked up.");

        if (!targets.Payload.TryGetProperty("targets", out var entries) || entries.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException("The targets document has no 'targets' object.");
        }

        // Ordinal lookup, because a target path is a byte string chosen by whoever built the release,
        // not a piece of prose. A case-insensitive match would let 'Fortiq.Service.exe' be answered by
        // an entry for 'fortiq.service.exe' - two different files as far as the signer is concerned.
        return entries.TryGetProperty(targetPath, out var entry)
            ? TufFileInfo.Read(entry, targetPath)
            : null;
    }

    /// <summary>
    /// Refuses <paramref name="content"/> unless the trusted targets document names it at
    /// <paramref name="targetPath"/> with exactly this length and hash.
    /// </summary>
    public TufFileInfo RequireTarget(string targetPath, ReadOnlySpan<byte> content)
    {
        var info = FindTarget(targetPath)
            ?? throw new TufMetadataException(
                $"The trusted targets document names no target '{targetPath}', so nothing authorises installing it.");

        info.RequireMatch(content, targetPath);
        return info;
    }

    private SignedMetadata Accept(ReadOnlySpan<byte> document, TufRole role, DateTimeOffset now)
    {
        var metadata = SignedMetadata.Parse(document);
        var name = TufRoot.Name(role);

        if (!string.Equals(metadata.Type, name, StringComparison.Ordinal))
        {
            throw new TufMetadataException($"A document of type '{metadata.Type}' was served as the '{name}' role.");
        }

        _root.RequireSignatures(metadata, role);
        RequireUnexpired(metadata.Expires, role, now);
        return metadata;
    }

    private static void RequireUnexpired(DateTimeOffset expires, TufRole role, DateTimeOffset now)
    {
        if (expires <= now)
        {
            throw new TufMetadataException(
                $"The '{TufRoot.Name(role)}' document expired at {expires:O}, and it is {now:O}. " +
                "Either the update service has stopped publishing, or this machine's clock is wrong.");
        }
    }

    private static (long Version, TufFileInfo? Info) ReadMetaEntry(
        SignedMetadata metadata,
        string fileName,
        TufRole role)
    {
        if (!metadata.Payload.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException($"The '{TufRoot.Name(role)}' document has no 'meta' object.");
        }

        if (!meta.TryGetProperty(fileName, out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException($"The '{TufRoot.Name(role)}' document does not describe '{fileName}'.");
        }

        if (!entry.TryGetProperty("version", out var versionValue) ||
            versionValue.ValueKind != JsonValueKind.Number ||
            !versionValue.TryGetInt64(out var version) ||
            version < 1)
        {
            throw new TufMetadataException($"The entry for '{fileName}' has no positive integer 'version'.");
        }

        // Length and hashes are optional in the specification for these entries, and the version match
        // alone already blocks rollback. Where they are present they are enforced, which is strictly
        // more than the version check gives: it pins the bytes, not just the number they claim.
        var info = entry.TryGetProperty("hashes", out _)
            ? TufFileInfo.Read(entry, fileName)
            : null;

        return (version, info);
    }
}
