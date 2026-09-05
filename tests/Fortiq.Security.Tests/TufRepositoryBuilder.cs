using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fortiq.Infrastructure.Updates;

namespace Fortiq.Security.Tests;

/// <summary>A signing key pair for tests, and the key object a root document files it under.</summary>
internal sealed class TufTestKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TufTestKey()
    {
        var publicMaterial = Convert.ToHexStringLower(_key.ExportSubjectPublicKeyInfo());
        KeyObject = $$"""
            {"keytype":"ecdsa","keyval":{"public":"{{publicMaterial}}"},"scheme":"ecdsa-sha2-nistp256"}
            """;

        using var document = JsonDocument.Parse(KeyObject);
        KeyId = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Encode(document.RootElement)));
    }

    public string KeyId { get; }

    public string KeyObject { get; }

    public string Sign(byte[] payload) => Convert.ToHexStringLower(
        _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Builds the four role documents a TUF client consumes, so that a test can serve a deliberately
/// wrong one and assert the client refuses it.
/// </summary>
/// <remarks>
/// Written as a builder over raw JSON rather than over the production types on purpose. A test that
/// constructed metadata with the same code that reads it could only ever prove the code agrees with
/// itself; the attacks below need documents the production code would never produce.
/// </remarks>
internal static class TufRepositoryBuilder
{
    public static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    public static string Expiry(TimeSpan fromNow) => (Now + fromNow).ToString("O");

    /// <summary>Wraps a role document in the signatures of every key given.</summary>
    public static byte[] Sign(string signedJson, params TufTestKey[] keys)
    {
        using var document = JsonDocument.Parse(signedJson);
        var payload = CanonicalJson.Encode(document.RootElement);

        var signatures = string.Join(
            ",",
            keys.Select(key => $$"""{"keyid":"{{key.KeyId}}","sig":"{{key.Sign(payload)}}"}"""));

        return Encoding.UTF8.GetBytes($$"""
            {"signatures":[{{signatures}}],"signed":{{signedJson}}}
            """);
    }

    public static string Root(
        long version,
        IReadOnlyList<TufTestKey> rootKeys,
        TufTestKey roleKey,
        int rootThreshold = 1,
        string? expires = null)
    {
        var keys = rootKeys.Append(roleKey).DistinctBy(key => key.KeyId).ToList();
        var keyEntries = string.Join(",", keys.Select(key => Quote(key.KeyId) + ":" + key.KeyObject));
        var rootIds = string.Join(",", rootKeys.Select(key => Quote(key.KeyId)));
        var roleId = Quote(roleKey.KeyId);

        // Concatenated rather than interpolated. Every document here is JSON, so an interpolated
        // literal spends more of its length escaping braces than saying what the document is.
        return "{" +
            Field("_type", "root") + "," +
            Field("expires", expires ?? Expiry(TimeSpan.FromDays(365))) + "," +
            Quote("keys") + ":{" + keyEntries + "}," +
            Quote("roles") + ":{" +
                Quote("root") + ":" + RoleTrust(rootIds, rootThreshold) + "," +
                Quote("snapshot") + ":" + RoleTrust(roleId, 1) + "," +
                Quote("targets") + ":" + RoleTrust(roleId, 1) + "," +
                Quote("timestamp") + ":" + RoleTrust(roleId, 1) +
            "}," +
            Number("version", version) +
            "}";
    }

    public static string Timestamp(long version, long snapshotVersion, string? expires = null) =>
        MetaDocument("timestamp", version, expires ?? Expiry(TimeSpan.FromDays(1)), "snapshot.json", snapshotVersion);

    public static string Snapshot(long version, long targetsVersion, string? expires = null) =>
        MetaDocument("snapshot", version, expires ?? Expiry(TimeSpan.FromDays(7)), "targets.json", targetsVersion);

    public static string Targets(
        long version,
        string targetPath,
        ReadOnlySpan<byte> content,
        string? expires = null)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(content));

        return "{" +
            Field("_type", "targets") + "," +
            Field("expires", expires ?? Expiry(TimeSpan.FromDays(30))) + "," +
            Quote("targets") + ":{" +
                Quote(targetPath) + ":{" +
                    Quote("hashes") + ":{" + Field("sha256", digest) + "}," +
                    Number("length", content.Length) +
                "}" +
            "}," +
            Number("version", version) +
            "}";
    }

    private static string MetaDocument(string type, long version, string expires, string describes, long describedVersion) =>
        "{" +
        Field("_type", type) + "," +
        Field("expires", expires) + "," +
        Quote("meta") + ":{" + Quote(describes) + ":{" + Number("version", describedVersion) + "}}," +
        Number("version", version) +
        "}";

    private static string RoleTrust(string keyIds, int threshold) =>
        "{" + Quote("keyids") + ":[" + keyIds + "]," + Number("threshold", threshold) + "}";

    private static string Quote(string value) => "\"" + value + "\"";

    private static string Field(string name, string value) => Quote(name) + ":" + Quote(value);

    private static string Number(string name, long value) => Quote(name) + ":" + value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Drives a client through a complete, well-formed update to the release described.</summary>
    public static TufTrustedMetadata Advance(
        TufTrustedMetadata client,
        TufTestKey roleKey,
        long releaseVersion,
        string targetPath,
        byte[] content)
    {
        client.UpdateTimestamp(Sign(Timestamp(releaseVersion, releaseVersion), roleKey), Now);
        client.UpdateSnapshot(Sign(Snapshot(releaseVersion, releaseVersion), roleKey), Now);
        client.UpdateTargets(Sign(Targets(releaseVersion, targetPath, content), roleKey), Now);
        return client;
    }
}
