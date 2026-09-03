using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fortiq.Infrastructure.Restic;

public sealed record EngineManifest(
    string Schema,
    int Version,
    IReadOnlyList<EngineManifestEntry> Engines);

public sealed record EngineManifestEntry(
    string Name,
    string Version,
    string Rid,
    string RelativePath,
    long BinaryLength,
    string BinarySha256,
    string ArchiveSha256,
    string SourceUrl,
    string License,
    string UpstreamCommit);

public static partial class EngineManifestReader
{
    private const string ExpectedSchema = "fortiq.engine-manifest";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<EngineManifest> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<EngineManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Engine manifest is empty.");

        Validate(manifest);
        return manifest;
    }

    private static void Validate(EngineManifest manifest)
    {
        if (!string.Equals(manifest.Schema, ExpectedSchema, StringComparison.Ordinal) || manifest.Version != 1)
        {
            throw new InvalidDataException("Unsupported engine manifest schema or version.");
        }

        if (manifest.Engines.Count == 0)
        {
            throw new InvalidDataException("Engine manifest must contain at least one entry.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Engines)
        {
            if (entry.Name != "restic" || string.IsNullOrWhiteSpace(entry.Version) || string.IsNullOrWhiteSpace(entry.Rid))
            {
                throw new InvalidDataException("Engine identity is invalid or unsupported.");
            }

            if (Path.IsPathRooted(entry.RelativePath) || entry.RelativePath.Split('/', '\\').Any(part => part is ".." or "." or ""))
            {
                throw new InvalidDataException("Engine path must be a normalized relative path.");
            }

            if (entry.BinaryLength <= 0 || !Sha256Regex().IsMatch(entry.BinarySha256) || !Sha256Regex().IsMatch(entry.ArchiveSha256))
            {
                throw new InvalidDataException("Engine length or SHA-256 is invalid.");
            }

            if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Engine source URL must use HTTPS.");
            }

            if (!identities.Add($"{entry.Name}\0{entry.Version}\0{entry.Rid}"))
            {
                throw new InvalidDataException("Engine manifest contains a duplicate identity.");
            }
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
