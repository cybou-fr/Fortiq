using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fortiq.Assistant;

/// <summary>The inference runtimes this build is willing to start.</summary>
public sealed record RuntimeManifest(
    string Schema,
    int Version,
    IReadOnlyList<RuntimeManifestEntry> Runtimes);

/// <summary>
/// One pinned llama.cpp build.
/// </summary>
/// <param name="Name">The runtime as Fortiq refers to it.</param>
/// <param name="Version">The upstream build, which is what makes this reproducible.</param>
/// <param name="Rid">Which platform this build is for.</param>
/// <param name="RelativePath">The server executable, under the runtime root.</param>
/// <param name="ArchiveSha256">The hash of the published archive, checked before extraction.</param>
/// <param name="ArchiveLength">The archive's exact length.</param>
/// <param name="SourceUrl">Where the archive is fetched from, over HTTPS and nowhere else.</param>
/// <param name="License">The runtime's licence, which ships beside it.</param>
/// <remarks>
/// The archive is what carries a hash here, and the executable does not. That is not an oversight:
/// <c>llama-server.exe</c> is a nine-kilobyte launcher and every line that matters lives in the DLLs
/// beside it, so a hash of the executable would look like a supply-chain control and be none. The
/// archive hash is checked at the one moment the whole tree is still a single object; after
/// installation, per-file integrity is the deployment bundle's manifest.
/// </remarks>
public sealed record RuntimeManifestEntry(
    string Name,
    string Version,
    string Rid,
    string RelativePath,
    string ArchiveSha256,
    long ArchiveLength,
    string SourceUrl,
    string License);

/// <summary>Whether the assistant's runtime is on this machine, and what to say when it is not.</summary>
public sealed record RuntimeStatus(ModelPresence Presence, string? Detail, string? Path, RuntimeManifestEntry? Entry)
{
    public bool Usable => Presence == ModelPresence.Present;
}

/// <summary>
/// Finds the llama.cpp build the assistant runs on, and says plainly when it is not there.
/// </summary>
/// <remarks>
/// The same question <see cref="ModelAvailability"/> asks about the weights, asked about the thing
/// that runs them, and for the same reason: both arrive in the installation package or during
/// installation, and a machine missing either should say so on launch rather than partway into a
/// screen somebody opened because they already had a problem.
///
/// Length is not checked, unlike the model. Nothing here is one file - it is a folder of an
/// executable and its libraries - so presence is what can be answered cheaply, and the deployment
/// bundle's per-file hashes are what answer the rest.
/// </remarks>
public static class RuntimeAvailability
{
    /// <summary>The runtime directory beside an installation, by convention.</summary>
    public const string DirectoryName = "runtimes";

    private const string ExpectedSchema = "fortiq.runtime-manifest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<RuntimeStatus> InspectAsync(string runtimeRoot, string rid, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);

        var manifestPath = Path.Combine(runtimeRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new RuntimeStatus(
                ModelPresence.NoManifest,
                $"Fortiq cannot find the description of its assistant runtime at '{manifestPath}'. "
                + "Install the Fortiq release again - the runtime is part of the package.",
                null,
                null);
        }

        RuntimeManifest manifest;
        try
        {
            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest = await JsonSerializer.DeserializeAsync<RuntimeManifest>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Runtime manifest is empty.");
            Validate(manifest);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return new RuntimeStatus(
                ModelPresence.ManifestUnusable,
                $"Fortiq could not read the description of its assistant runtime: {error.Message} "
                + "This copy of Fortiq is damaged; install the release again.",
                null,
                null);
        }

        var entry = manifest.Runtimes.FirstOrDefault(candidate => string.Equals(candidate.Rid, rid, StringComparison.Ordinal));
        if (entry is null)
        {
            return new RuntimeStatus(
                ModelPresence.ManifestUnusable,
                $"This build of Fortiq has no assistant runtime for {rid}.",
                null,
                null);
        }

        var path = Path.GetFullPath(Path.Combine(runtimeRoot, entry.RelativePath));
        var root = Path.GetFullPath(runtimeRoot);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeStatus(
                ModelPresence.ManifestUnusable,
                $"The runtime description at '{manifestPath}' points outside the runtime folder. "
                + "This copy of Fortiq is damaged; install the release again.",
                null,
                entry);
        }

        return File.Exists(path)
            ? new RuntimeStatus(ModelPresence.Present, null, path, entry)
            : new RuntimeStatus(
                ModelPresence.Missing,
                $"Fortiq's assistant runtime is missing. It should be at '{path}'. Install the Fortiq "
                + "release again, or run the runtime acquisition step, and it will be fetched and verified.",
                path,
                entry);
    }

    private static void Validate(RuntimeManifest manifest)
    {
        if (!string.Equals(manifest.Schema, ExpectedSchema, StringComparison.Ordinal) || manifest.Version != 1)
        {
            throw new InvalidDataException("Unsupported runtime manifest schema or version.");
        }

        if (manifest.Runtimes.Count == 0)
        {
            throw new InvalidDataException("Runtime manifest must contain at least one entry.");
        }

        foreach (var entry in manifest.Runtimes)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.Version)
                || string.IsNullOrWhiteSpace(entry.Rid)
                || string.IsNullOrWhiteSpace(entry.License))
            {
                throw new InvalidDataException("Runtime identity is incomplete.");
            }

            if (Path.IsPathRooted(entry.RelativePath)
                || entry.RelativePath.Split('/', '\\').Any(part => part is ".." or "." or ""))
            {
                throw new InvalidDataException("Runtime path must be a normalized relative path.");
            }

            if (entry.ArchiveLength <= 0 || entry.ArchiveSha256.Length != 64)
            {
                throw new InvalidDataException("Runtime archive length or SHA-256 is invalid.");
            }

            if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Runtime source URL must use HTTPS.");
            }
        }
    }
}
