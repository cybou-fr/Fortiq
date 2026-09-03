using System.Security.Cryptography;

namespace Fortiq.Infrastructure.Restic;

public sealed record VerifiedEngine(
    string Name,
    string Version,
    string Rid,
    string AbsolutePath,
    string Sha256);

public static class EngineBinaryVerifier
{
    public static async Task<VerifiedEngine> VerifyAsync(
        string engineRoot,
        EngineManifestEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentNullException.ThrowIfNull(entry);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineRoot));
        var binaryPath = Path.GetFullPath(Path.Combine(canonicalRoot, entry.RelativePath));
        var requiredPrefix = canonicalRoot + Path.DirectorySeparatorChar;

        if (!binaryPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Engine binary resolves outside the configured engine root.");
        }

        var file = new FileInfo(binaryPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Pinned engine binary is missing.", binaryPath);
        }

        if (file.LinkTarget is not null)
        {
            throw new InvalidDataException("Engine binary cannot be a symbolic link.");
        }

        if (file.Length != entry.BinaryLength)
        {
            throw new InvalidDataException("Engine binary length does not match the manifest.");
        }

        await using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexStringLower(digest);

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(entry.BinarySha256)))
        {
            throw new InvalidDataException("Engine binary SHA-256 does not match the manifest.");
        }

        return new VerifiedEngine(entry.Name, entry.Version, entry.Rid, binaryPath, actualHash);
    }
}
