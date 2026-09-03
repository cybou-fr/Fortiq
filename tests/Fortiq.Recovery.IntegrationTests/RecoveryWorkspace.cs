using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// An isolated temporary workspace plus the pinned engine the recovery tests run against. Tests
/// never fall back to a globally installed restic: when the pinned binary is absent the caller skips.
/// </summary>
internal sealed class RecoveryWorkspace : IDisposable
{
    /// <summary>
    /// Set to a directory to keep the receipts a run produced. CI points it at its artifact
    /// directory so evidence survives a failing job; locally the workspace is simply deleted.
    /// </summary>
    private const string ArtifactDirectoryVariable = "FORTIQ_TEST_ARTIFACTS";

    private readonly string _name;

    private RecoveryWorkspace(string name, string root, VerifiedEngine engine)
    {
        _name = name;
        Root = root;
        Engine = engine;
    }

    public string Root { get; }

    public VerifiedEngine Engine { get; }

    public static async Task<RecoveryWorkspace> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var engine = await VerifyPinnedEngineAsync(cancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"fortiq-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new RecoveryWorkspace(name, root, engine);
    }

    public string EnsureDirectory(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>An adapter with its own Fortiq working directory, as a separate run would have.</summary>
    internal ResticRepositoryEngine Adapter(string stateDirectory, IEngineCredentialProvider? credentials = null) =>
        new(
            Engine,
            new ResticProcessRunner(),
            credentials ?? new InsecureNoPasswordCredentialProvider(),
            EnsureDirectory(stateDirectory));

    /// <summary>The engine root of the repository checkout, with its manifest and pinned binary.</summary>
    public static string EngineRootPath => Path.Combine(FindRepositoryRoot(), "engines");

    /// <summary>The directory receipts are written to, as a Fortiq run would own it.</summary>
    public string ReceiptDirectory => EnsureDirectory("receipts");

    /// <summary>An adapter that also records one receipt per operation.</summary>
    internal IBackupRepository RecordingAdapter(string stateDirectory, IEngineCredentialProvider? credentials = null) =>
        new ReceiptRecordingBackupRepository(
            Adapter(stateDirectory, credentials),
            new EngineIdentity(Engine.Name, Engine.Version, Engine.Sha256),
            new FileSystemOperationReceiptStore(ReceiptDirectory));

    /// <summary>Reads every receipt written so far, newest last.</summary>
    public IReadOnlyList<JsonElement> Receipts()
    {
        var documents = new List<JsonElement>();
        foreach (var path in Directory.EnumerateFiles(ReceiptDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            documents.Add(JsonDocument.Parse(File.ReadAllText(path)).RootElement);
        }

        return documents;
    }

    public void Dispose()
    {
        Engine.Dispose();
        PreserveReceipts();
        TestDataset.MakeWritable(Root);
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temporary file must not turn a passing recovery test into a failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same for a file whose attributes the engine restored as read-only.
        }
    }

    private void PreserveReceipts()
    {
        var artifacts = Environment.GetEnvironmentVariable(ArtifactDirectoryVariable);
        var receipts = Path.Combine(Root, "receipts");
        if (string.IsNullOrWhiteSpace(artifacts) || !Directory.Exists(receipts))
        {
            return;
        }

        var destination = Path.Combine(artifacts, _name);
        Directory.CreateDirectory(destination);
        foreach (var receipt in Directory.EnumerateFiles(receipts, "*.json"))
        {
            File.Copy(receipt, Path.Combine(destination, Path.GetFileName(receipt)), overwrite: true);
        }
    }

    private static async Task<VerifiedEngine> VerifyPinnedEngineAsync(CancellationToken cancellationToken)
    {
        var engineRoot = EngineRootPath;
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(engineRoot, "manifest.json"), cancellationToken);
        var entry = manifest.Engines.Single(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64");
        Skip.IfNot(
            File.Exists(Path.Combine(engineRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
            "Pinned restic binary is not present in engines/; acquisition is a separate step.");

        return await EngineBinaryVerifier.VerifyAsync(engineRoot, entry, cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fortiq.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
