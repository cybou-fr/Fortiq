namespace Fortiq.Domain;

/// <summary>Where a repository lives: a directory on this machine, or a bucket somewhere else.</summary>
public enum RepositoryLocationKind
{
    LocalDirectory,
    ObjectStorage
}

/// <summary>
/// A repository location, normalised for what it is. A local path is made absolute; an object
/// storage URL is left exactly as written, because it is not a path and treating it as one turns
/// <c>s3:https://host/bucket</c> into nonsense.
/// </summary>
public static class RepositoryLocation
{
    private const string ObjectStoragePrefix = "s3:";

    public static RepositoryLocationKind KindOf(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return location.StartsWith(ObjectStoragePrefix, StringComparison.OrdinalIgnoreCase)
            ? RepositoryLocationKind.ObjectStorage
            : RepositoryLocationKind.LocalDirectory;
    }

    public static bool IsObjectStorage(string location) => KindOf(location) == RepositoryLocationKind.ObjectStorage;

    /// <summary>The form the engine is given. Only a local path is resolved.</summary>
    public static string Normalize(string location) => KindOf(location) switch
    {
        RepositoryLocationKind.LocalDirectory => Path.GetFullPath(location),
        _ => Require(location)
    };

    /// <summary>
    /// Checks that an object storage location is one this build can hand to the engine, and says so
    /// plainly when it is not. A malformed location would otherwise surface as an engine error whose
    /// cause is a typo.
    /// </summary>
    private static string Require(string location)
    {
        var remainder = location[ObjectStoragePrefix.Length..];
        if (remainder.Length == 0)
        {
            throw new ArgumentException("An object storage location must name a bucket.", nameof(location));
        }

        // Both forms restic accepts: an endpoint URL with a bucket, or a bucket on the default
        // endpoint. Anything else is refused rather than passed through and hoped for.
        var withoutScheme = remainder.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || remainder.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? remainder[(remainder.IndexOf("://", StringComparison.Ordinal) + 3)..]
                : remainder;

        return withoutScheme.Contains('/', StringComparison.Ordinal) || !withoutScheme.Contains('.', StringComparison.Ordinal)
            ? location
            : throw new ArgumentException("An object storage location must name a bucket after its host.", nameof(location));
    }
}
