using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fortiq.Infrastructure.Keys;

/// <summary>One way to unlock the repository, as recorded in a recovery kit.</summary>
public sealed record RecoveryKitUnlockMethod(
    string File,
    string ProviderType,
    string Suite,
    string EnvelopeId,
    string Sha256);

/// <summary>
/// The public part of a recovery kit: where the repository is, which engine wrote it, and which
/// unlock methods exist. It holds no secret and no recovery material - the mnemonic is never written
/// into it, and the envelopes it points at are useless without one.
/// </summary>
public sealed record RecoveryKit(
    string RepositoryId,
    string RepositoryLocator,
    RecoveryKitEngine Engine,
    DateTimeOffset CreatedAt,
    IReadOnlyList<RecoveryKitUnlockMethod> UnlockMethods,
    string Instructions)
{
    public const string Schema = "fortiq.recovery-kit";
    public const int SchemaVersion = 1;
    public const string ManifestFileName = "kit.json";
}

public sealed record RecoveryKitEngine(string Name, string Version, string Sha256);

/// <summary>An opened kit: the manifest plus the envelopes it points at, already verified.</summary>
public sealed record OpenedRecoveryKit(RecoveryKit Manifest, IReadOnlyList<KeyEnvelopeV1> Envelopes);

public static class RecoveryKitStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// Writes a kit directory: one file per envelope plus the manifest. The manifest is written last,
    /// so a kit that has a manifest has the envelopes it names.
    /// </summary>
    public static async Task<RecoveryKit> WriteAsync(
        string directory,
        string repositoryLocator,
        RecoveryKitEngine engine,
        IReadOnlyList<KeyEnvelopeV1> envelopes,
        TimeProvider? clock,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count == 0)
        {
            throw new ArgumentException("A recovery kit must contain at least one unlock method.", nameof(envelopes));
        }

        var repositoryId = Convert.ToHexStringLower(envelopes[0].RepositoryId);
        if (envelopes.Any(envelope => Convert.ToHexStringLower(envelope.RepositoryId) != repositoryId))
        {
            throw new ArgumentException("All envelopes in a kit must belong to the same repository.", nameof(envelopes));
        }

        Directory.CreateDirectory(directory);

        var methods = new List<RecoveryKitUnlockMethod>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            var encoded = KeyEnvelopeCodec.Encode(envelope);
            var fileName = $"{envelope.ProviderType.ToString().ToLowerInvariant()}-{Convert.ToHexStringLower(envelope.EnvelopeId)}.cbor";
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), encoded, cancellationToken);

            methods.Add(new RecoveryKitUnlockMethod(
                fileName,
                envelope.ProviderType.ToString().ToLowerInvariant(),
                envelope.Suite,
                Convert.ToHexStringLower(envelope.EnvelopeId),
                Convert.ToHexStringLower(SHA256.HashData(encoded))));
        }

        var kit = new RecoveryKit(
            repositoryId,
            repositoryLocator,
            engine,
            DateTimeOffset.FromUnixTimeSeconds((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()),
            methods,
            "Restore with: Fortiq.Recover restore --kit <this directory> --repository <repository> "
            + "--engine-root <engines> --snapshot <id> --target <empty directory>. "
            + "The recovery mnemonic is typed on standard input and is not stored in this kit.");

        var document = new ManifestDocument(
            RecoveryKit.Schema,
            RecoveryKit.SchemaVersion,
            kit.RepositoryId,
            kit.RepositoryLocator,
            kit.Engine,
            kit.CreatedAt,
            kit.UnlockMethods,
            kit.Instructions);

        await File.WriteAllTextAsync(
            Path.Combine(directory, RecoveryKit.ManifestFileName),
            JsonSerializer.Serialize(document, SerializerOptions),
            cancellationToken);

        return kit;
    }

    /// <summary>
    /// Reads a kit and verifies it before returning: the manifest schema, the hash of every envelope
    /// file, that each envelope decodes, and that it belongs to the repository the manifest names.
    /// </summary>
    public static async Task<OpenedRecoveryKit> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var manifestPath = Path.Combine(directory, RecoveryKit.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The recovery kit has no manifest.", manifestPath);
        }

        var document = JsonSerializer.Deserialize<ManifestDocument>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            ReaderOptions) ?? throw new InvalidDataException("The recovery kit manifest is empty.");

        if (document.Schema != RecoveryKit.Schema || document.Version != RecoveryKit.SchemaVersion)
        {
            throw new InvalidDataException("Unsupported recovery kit schema or version.");
        }

        if (document.UnlockMethods.Count == 0)
        {
            throw new InvalidDataException("The recovery kit lists no unlock method.");
        }

        var envelopes = new List<KeyEnvelopeV1>(document.UnlockMethods.Count);
        foreach (var method in document.UnlockMethods)
        {
            if (Path.GetFileName(method.File) != method.File)
            {
                throw new InvalidDataException("A recovery kit entry must name a file inside the kit.");
            }

            var encoded = await File.ReadAllBytesAsync(Path.Combine(directory, method.File), cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(encoded), Convert.FromHexString(method.Sha256)))
            {
                throw new InvalidDataException("A recovery kit envelope does not match the hash in its manifest.");
            }

            var envelope = KeyEnvelopeCodec.Decode(encoded);
            if (Convert.ToHexStringLower(envelope.RepositoryId) != document.RepositoryId
                || Convert.ToHexStringLower(envelope.EnvelopeId) != method.EnvelopeId
                || envelope.Suite != method.Suite)
            {
                throw new InvalidDataException("A recovery kit envelope contradicts the manifest that lists it.");
            }

            envelopes.Add(envelope);
        }

        var kit = new RecoveryKit(
            document.RepositoryId,
            document.RepositoryLocator,
            document.Engine,
            document.CreatedAt,
            document.UnlockMethods,
            document.Instructions);

        return new OpenedRecoveryKit(kit, envelopes);
    }

    private sealed record ManifestDocument(
        string Schema,
        int Version,
        string RepositoryId,
        string RepositoryLocator,
        RecoveryKitEngine Engine,
        DateTimeOffset CreatedAt,
        IReadOnlyList<RecoveryKitUnlockMethod> UnlockMethods,
        string Instructions);
}
