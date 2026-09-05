using System.Text;
using Fortiq.Infrastructure.Updates;

namespace Fortiq.Security.Tests;

/// <summary>
/// The attacks ADR-008 says the update path must survive, each one served to a client that has already
/// accepted a legitimate release.
/// </summary>
/// <remarks>
/// Every test here asserts a refusal. That is deliberate: an updater whose happy path works is not the
/// interesting property - an updater that installs the wrong binary works too, right up until it
/// replaces a running backup service with something an attacker chose.
/// </remarks>
public sealed class TufUpdateTrustTests
{
    private const string TargetPath = "win-x64/Fortiq.Service.exe";

    private static readonly byte[] Release1 = Encoding.UTF8.GetBytes("the binary shipped in release 1");
    private static readonly byte[] Release2 = Encoding.UTF8.GetBytes("the binary shipped in release 2");

    [Fact]
    public void ACompleteReleaseIsAcceptedAndNamesItsTarget()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();

        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        TufRepositoryBuilder.Advance(client, roleKey, 1, TargetPath, Release1);

        var target = client.RequireTarget(TargetPath, Release1);

        Assert.Equal(Release1.Length, target.Length);
        Assert.Equal(1, client.TargetsVersion);
    }

    [Fact]
    public void ABinaryTheTargetsDocumentDoesNotNameIsRefused()
    {
        var client = TrustedClientAt(1, Release1, out var keys);
        using var _ = keys;

        var error = Assert.Throws<TufMetadataException>(
            () => client.RequireTarget("win-x64/Fortiq.Desktop.exe", Release1));

        Assert.Contains("names no target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABinaryThatDoesNotMatchTheRecordedHashIsRefused()
    {
        var client = TrustedClientAt(1, Release1, out var keys);
        using var _ = keys;

        var tampered = Release1.ToArray();
        tampered[0] ^= 0xFF;

        Assert.Throws<TufMetadataException>(() => client.RequireTarget(TargetPath, tampered));
    }

    [Fact]
    public void ABinaryOfTheWrongLengthIsRefusedEvenWhereThePrefixMatches()
    {
        var client = TrustedClientAt(1, Release1, out var keys);
        using var _ = keys;

        var padded = Release1.Concat(new byte[] { 0x00 }).ToArray();

        var error = Assert.Throws<TufMetadataException>(() => client.RequireTarget(TargetPath, padded));
        Assert.Contains("byte(s)", error.Message, StringComparison.Ordinal);
    }

    // --- Rollback -----------------------------------------------------------------------------

    [Fact]
    public void AnEarlierReleaseServedAfterALaterOneIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        TufRepositoryBuilder.Advance(client, roleKey, 2, TargetPath, Release2);

        // Release 1's metadata is genuine and correctly signed. It is simply old, which is exactly
        // what a rollback attack serves: a vulnerable version the publisher really did release.
        var error = Assert.Throws<TufMetadataException>(() => client.UpdateTimestamp(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Timestamp(1, 1), roleKey),
            TufRepositoryBuilder.Now));

        Assert.Contains("is already trusted", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, client.TimestampVersion);
    }

    [Fact]
    public void ASnapshotOlderThanTheTimestampNamesIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        client.UpdateTimestamp(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Timestamp(2, 2), roleKey),
            TufRepositoryBuilder.Now);

        var error = Assert.Throws<TufMetadataException>(() => client.UpdateSnapshot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Snapshot(1, 1), roleKey),
            TufRepositoryBuilder.Now));

        Assert.Contains("names version 2", error.Message, StringComparison.Ordinal);
    }

    // --- Mix and match ------------------------------------------------------------------------

    [Fact]
    public void TargetsFromAReleaseTheSnapshotDoesNotNameAreRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        client.UpdateTimestamp(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Timestamp(2, 2), roleKey),
            TufRepositoryBuilder.Now);
        client.UpdateSnapshot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Snapshot(2, 2), roleKey),
            TufRepositoryBuilder.Now);

        // Release 1's targets document, correctly signed, offered inside release 2. This is the
        // component mix-and-match ADR-008 names: each document is authentic, the combination is not.
        var error = Assert.Throws<TufMetadataException>(() => client.UpdateTargets(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Targets(1, TargetPath, Release1), roleKey),
            TufRepositoryBuilder.Now));

        Assert.Contains("names version 2", error.Message, StringComparison.Ordinal);
    }

    // --- Freeze -------------------------------------------------------------------------------

    [Fact]
    public void MetadataThatHasExpiredIsRefusedHoweverWellSigned()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        var timestamp = TufRepositoryBuilder.Sign(
            TufRepositoryBuilder.Timestamp(1, 1, TufRepositoryBuilder.Expiry(TimeSpan.FromDays(1))),
            roleKey);

        // A server that simply stops publishing keeps a client on a vulnerable release forever, and
        // every document it serves stays validly signed. Expiry is what turns that silence into a
        // refusal rather than an indefinite hold.
        var twoDaysLater = TufRepositoryBuilder.Now.AddDays(2);

        var error = Assert.Throws<TufMetadataException>(() => client.UpdateTimestamp(timestamp, twoDaysLater));
        Assert.Contains("expired", error.Message, StringComparison.Ordinal);
    }

    // --- Signatures ---------------------------------------------------------------------------

    [Fact]
    public void MetadataSignedByAKeyTheRootDoesNotTrustIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        using var attackerKey = new TufTestKey();

        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        var error = Assert.Throws<TufMetadataException>(() => client.UpdateTimestamp(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Timestamp(1, 1), attackerKey),
            TufRepositoryBuilder.Now));

        Assert.Contains("0 valid signature(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentAlteredAfterSigningIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        var signed = TufRepositoryBuilder.Sign(TufRepositoryBuilder.Timestamp(1, 1), roleKey);
        var altered = Encoding.UTF8.GetString(signed).Replace("\"version\":1}", "\"version\":9}", StringComparison.Ordinal);

        Assert.Throws<TufMetadataException>(
            () => client.UpdateTimestamp(Encoding.UTF8.GetBytes(altered), TufRepositoryBuilder.Now));
    }

    [Fact]
    public void OneKeyRepeatedDoesNotSatisfyAThresholdOfTwo()
    {
        using var first = new TufTestKey();
        using var second = new TufTestKey();
        using var roleKey = new TufTestKey();

        var root = TufRepositoryBuilder.Root(1, [first, second], roleKey, rootThreshold: 2);

        // Signed twice by the same key. Counting signatures rather than distinct keys would read this
        // as two of two, and a single compromised key would then be the whole root.
        var error = Assert.Throws<TufMetadataException>(
            () => TufTrustedMetadata.LoadTrustedRoot(TufRepositoryBuilder.Sign(root, first, first)));

        Assert.Contains("1 valid signature(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AThresholdOfTwoIsSatisfiedByTwoDistinctKeys()
    {
        using var first = new TufTestKey();
        using var second = new TufTestKey();
        using var roleKey = new TufTestKey();

        var root = TufRepositoryBuilder.Root(1, [first, second], roleKey, rootThreshold: 2);
        var client = TufTrustedMetadata.LoadTrustedRoot(TufRepositoryBuilder.Sign(root, first, second));

        Assert.Equal(1, client.RootVersion);
    }

    [Fact]
    public void AKeyFiledUnderAnIdentifierThatIsNotItsOwnIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        using var attackerKey = new TufTestKey();

        // The root names the key an attacker controls, but files it under the identifier the roles
        // trust. Reading the identifier from the document rather than computing it from the material
        // would make this substitution invisible.
        var root = TufRepositoryBuilder.Root(1, [rootKey], roleKey)
            .Replace($"\"{rootKey.KeyId}\":{rootKey.KeyObject}", $"\"{rootKey.KeyId}\":{attackerKey.KeyObject}", StringComparison.Ordinal);

        var error = Assert.Throws<TufMetadataException>(
            () => TufTrustedMetadata.LoadTrustedRoot(TufRepositoryBuilder.Sign(root, attackerKey)));

        Assert.Contains("identifies it as", error.Message, StringComparison.Ordinal);
    }

    // --- Role confusion -----------------------------------------------------------------------

    [Fact]
    public void ADocumentServedAsTheWrongRoleIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        // A genuine snapshot document, correctly signed, offered where a timestamp belongs. Both roles
        // are signed by the same key here, so only the type check stands between them.
        var error = Assert.Throws<TufMetadataException>(() => client.UpdateTimestamp(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Snapshot(1, 1), roleKey),
            TufRepositoryBuilder.Now));

        Assert.Contains("served as the 'timestamp' role", error.Message, StringComparison.Ordinal);
    }

    // --- Root rotation ------------------------------------------------------------------------

    [Fact]
    public void ARootRotationSignedOnlyByTheIncomingKeysIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var roleKey = new TufTestKey();
        using var attackerKey = new TufTestKey();

        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        // Anybody who can serve a file can produce this: a new root naming their own keys, signed with
        // them. Requiring the outgoing root's signature too is what makes it worthless.
        var takeover = TufRepositoryBuilder.Root(2, [attackerKey], roleKey);

        Assert.Throws<TufMetadataException>(
            () => client.UpdateRoot(TufRepositoryBuilder.Sign(takeover, attackerKey), TufRepositoryBuilder.Now));
    }

    [Fact]
    public void ARootRotationThatSkipsAVersionIsRefused()
    {
        using var rootKey = new TufTestKey();
        using var successorKey = new TufTestKey();
        using var roleKey = new TufTestKey();

        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        // Version 3 signed by both roots would otherwise be accepted, skipping whatever version 2
        // revoked. A revocation that can be stepped over is not a revocation.
        var skipped = TufRepositoryBuilder.Root(3, [successorKey], roleKey);

        var error = Assert.Throws<TufMetadataException>(
            () => client.UpdateRoot(TufRepositoryBuilder.Sign(skipped, rootKey, successorKey), TufRepositoryBuilder.Now));

        Assert.Contains("version 2 comes next", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootRotationSignedByBothRootsIsAcceptedAndDiscardsLowerMetadata()
    {
        using var rootKey = new TufTestKey();
        using var successorKey = new TufTestKey();
        using var roleKey = new TufTestKey();

        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        TufRepositoryBuilder.Advance(client, roleKey, 1, TargetPath, Release1);
        Assert.Equal(1, client.TargetsVersion);

        var rotated = TufRepositoryBuilder.Root(2, [successorKey], roleKey);
        client.UpdateRoot(TufRepositoryBuilder.Sign(rotated, rootKey, successorKey), TufRepositoryBuilder.Now);

        Assert.Equal(2, client.RootVersion);

        // The lower roles were validated against role definitions this rotation replaced, so they are
        // dropped rather than carried across a key change they were never checked against.
        Assert.Null(client.TargetsVersion);
        Assert.Null(client.SnapshotVersion);
        Assert.Null(client.TimestampVersion);
    }

    private static TufTrustedMetadata TrustedClientAt(long release, byte[] content, out IDisposable keys)
    {
        var rootKey = new TufTestKey();
        var roleKey = new TufTestKey();
        keys = new Keys(rootKey, roleKey);

        var client = TufTrustedMetadata.LoadTrustedRoot(
            TufRepositoryBuilder.Sign(TufRepositoryBuilder.Root(1, [rootKey], roleKey), rootKey));

        return TufRepositoryBuilder.Advance(client, roleKey, release, TargetPath, content);
    }

    private sealed record Keys(TufTestKey Root, TufTestKey Role) : IDisposable
    {
        public void Dispose()
        {
            Root.Dispose();
            Role.Dispose();
        }
    }
}
