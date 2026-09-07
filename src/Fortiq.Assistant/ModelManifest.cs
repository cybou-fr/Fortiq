using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fortiq.Assistant;

/// <summary>The models this build is willing to run, pinned exactly as the engine is.</summary>
public sealed record ModelManifest(
    string Schema,
    int Version,
    IReadOnlyList<ModelManifestEntry> Models);

/// <summary>
/// One local model: which file, how big, what hash, and where it legitimately came from.
/// </summary>
/// <param name="Name">The model as Fortiq refers to it, not as its publisher names the file.</param>
/// <param name="Version">The version of Fortiq's own packaging of it.</param>
/// <param name="Family">The upstream family, for anybody comparing against a benchmark.</param>
/// <param name="Quantization">Which quantisation this file is, because size and quality follow it.</param>
/// <param name="RelativePath">Where the file sits under the model root. Always relative, never rooted.</param>
/// <param name="FileLength">Exact length in bytes. A short file is an interrupted download.</param>
/// <param name="FileSha256">Exact content hash. This is what makes the file the one that was reviewed.</param>
/// <param name="SourceUrl">Where it is fetched from, over HTTPS and nowhere else.</param>
/// <param name="License">The model's licence, which ships beside it.</param>
/// <param name="ContextTokens">The context window this profile is configured for.</param>
public sealed record ModelManifestEntry(
    string Name,
    string Version,
    string Family,
    string Quantization,
    string RelativePath,
    long FileLength,
    string FileSha256,
    string SourceUrl,
    string License,
    int ContextTokens);

/// <summary>
/// Reads and validates the model manifest.
/// </summary>
/// <remarks>
/// Deliberately the same shape and the same strictness as <c>EngineManifestReader</c>. A local model
/// is a large binary this product downloads and then runs on somebody's machine, which is exactly what
/// the engine is, and the reasons for pinning it are the same ones: an unpinned download is whatever
/// the server felt like serving today, and a hash checked after extraction is the only thing that
/// makes "the model we reviewed" and "the model we ran" the same object.
///
/// It is stricter than it needs to be about paths and schemes for the same reason the engine reader is:
/// this file decides what gets executed, and a manifest is data from outside.
/// </remarks>
public static partial class ModelManifestReader
{
    private const string ExpectedSchema = "fortiq.model-manifest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<ModelManifest> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<ModelManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Model manifest is empty.");

        Validate(manifest);
        return manifest;
    }

    private static void Validate(ModelManifest manifest)
    {
        if (!string.Equals(manifest.Schema, ExpectedSchema, StringComparison.Ordinal) || manifest.Version != 1)
        {
            throw new InvalidDataException("Unsupported model manifest schema or version.");
        }

        if (manifest.Models.Count == 0)
        {
            throw new InvalidDataException("Model manifest must contain at least one entry.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.Version)
                || string.IsNullOrWhiteSpace(entry.Family)
                || string.IsNullOrWhiteSpace(entry.Quantization))
            {
                throw new InvalidDataException("Model identity is incomplete.");
            }

            if (Path.IsPathRooted(entry.RelativePath)
                || entry.RelativePath.Split('/', '\\').Any(part => part is ".." or "." or ""))
            {
                throw new InvalidDataException("Model path must be a normalized relative path.");
            }

            if (entry.FileLength <= 0 || !Sha256Regex().IsMatch(entry.FileSha256))
            {
                throw new InvalidDataException("Model length or SHA-256 is invalid.");
            }

            if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Model source URL must use HTTPS.");
            }

            if (string.IsNullOrWhiteSpace(entry.License))
            {
                throw new InvalidDataException("A model ships with its licence or it does not ship.");
            }

            if (entry.ContextTokens <= 0)
            {
                throw new InvalidDataException("Model context window must be a positive number of tokens.");
            }

            if (!identities.Add($"{entry.Name}\0{entry.Version}"))
            {
                throw new InvalidDataException("Model manifest contains a duplicate identity.");
            }
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
