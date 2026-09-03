using System.Security.Cryptography;
using Fortiq.Platform.Windows;

namespace Fortiq.Infrastructure.Restic;

/// <summary>
/// An engine binary that was verified against the manifest, together with the open handle that keeps
/// it verified. The handle denies writes and deletes for as long as the engine is in use, so the
/// file that was hashed is the file that runs.
/// </summary>
public sealed class VerifiedEngine : IDisposable
{
    private readonly PinnedFile? _pin;

    internal VerifiedEngine(
        string name,
        string version,
        string rid,
        string absolutePath,
        string sha256,
        PinnedFile? pin = null)
    {
        Name = name;
        Version = version;
        Rid = rid;
        AbsolutePath = absolutePath;
        Sha256 = sha256;
        _pin = pin;
    }

    public string Name { get; }

    public string Version { get; }

    public string Rid { get; }

    public string AbsolutePath { get; }

    public string Sha256 { get; }

    /// <summary>
    /// Confirms, immediately before the binary is executed, that the path still resolves to the file
    /// that was verified. The open handle already prevents that file from being replaced in place;
    /// this closes the remaining gap, where a parent directory is swapped so the same path leads to
    /// a different file.
    /// </summary>
    internal void EnsureUnchangedForExecution()
    {
        if (_pin is not null && !_pin.IsSameFileAs(AbsolutePath))
        {
            throw new InvalidDataException(
                "The engine binary at the verified path is no longer the file that was verified.");
        }
    }

    public void Dispose() => _pin?.Dispose();
}

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

        // The handle is opened once and kept: it is what the hash is computed from, and holding it
        // denies writes and deletes for as long as the engine is in use, so the binary cannot be
        // swapped between this verification and its execution.
        var pin = PinnedFile.Open(binaryPath);
        try
        {
            if (pin.Length != entry.BinaryLength)
            {
                throw new InvalidDataException("Engine binary length does not match the manifest.");
            }

            var digest = await SHA256.HashDataAsync(pin.Content, cancellationToken);
            var actualHash = Convert.ToHexStringLower(digest);

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(entry.BinarySha256)))
            {
                throw new InvalidDataException("Engine binary SHA-256 does not match the manifest.");
            }

            return new VerifiedEngine(entry.Name, entry.Version, entry.Rid, binaryPath, actualHash, pin);
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }
}
