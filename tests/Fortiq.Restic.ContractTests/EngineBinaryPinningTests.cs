using System.Security.Cryptography;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

/// <summary>
/// The window between verifying the engine binary and executing it: the verified file must stay the
/// file that runs, and a path that has come to mean something else must be refused.
/// </summary>
public sealed class EngineBinaryPinningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fortiq-pin-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AVerifiedBinaryCannotBeOverwrittenOrDeletedWhileItIsHeld()
    {
        var (entry, path) = CreateEngine("original engine"u8.ToArray());
        using var engine = await EngineBinaryVerifier.VerifyAsync(_root, entry, CancellationToken.None);

        Assert.Throws<IOException>(() => File.WriteAllBytes(path, "swapped engine"u8.ToArray()));
        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));

        // The check that runs immediately before execution still recognises the same file.
        engine.EnsureUnchangedForExecution();
    }

    [Fact]
    public async Task ThePinIsReleasedWhenTheEngineIsDisposed()
    {
        var (entry, path) = CreateEngine("original engine"u8.ToArray());
        var engine = await EngineBinaryVerifier.VerifyAsync(_root, entry, CancellationToken.None);

        engine.Dispose();

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TheDirectoryHoldingAPinnedBinaryCannotBeRenamedAway()
    {
        var (entry, path) = CreateEngine("original engine"u8.ToArray());
        using var engine = await EngineBinaryVerifier.VerifyAsync(_root, entry, CancellationToken.None);

        // Windows refuses to rename a directory that contains an open file, so the simplest way to
        // make the path mean something else is already blocked by the pin itself.
        var binaryDirectory = Path.GetDirectoryName(path)!;
        Assert.Throws<IOException>(() => Directory.Move(binaryDirectory, binaryDirectory + "-displaced"));
    }

    [SkippableFact]
    public async Task APathThatComesToMeanADifferentFileIsRefusedBeforeExecution()
    {
        var (entry, _) = CreateEngine("original engine"u8.ToArray());
        var realRoot = Path.Combine(_root, "real");
        Directory.Move(Path.Combine(_root, "restic"), Path.Combine(EnsureDirectory(realRoot), "restic"));
        var realPath = Path.Combine(realRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        // A junction above the engine root can be repointed even while the binary is held open, so
        // the path can still come to mean a different file. This is what the identity check catches.
        var linkedRoot = Path.Combine(_root, "linked");
        Skip.IfNot(TryCreateJunction(linkedRoot, realRoot), "Creating a junction is not permitted here.");

        using var engine = await EngineBinaryVerifier.VerifyAsync(linkedRoot, entry, CancellationToken.None);
        engine.EnsureUnchangedForExecution();

        var decoyRoot = EnsureDirectory(Path.Combine(_root, "decoy"));
        var decoyBinary = Path.Combine(decoyRoot, "restic", "0.19.1", "win-x64", "restic.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(decoyBinary)!);
        await File.WriteAllBytesAsync(decoyBinary, "swapped engine"u8.ToArray());

        Directory.Delete(linkedRoot);
        Skip.IfNot(TryCreateJunction(linkedRoot, decoyRoot), "Recreating the junction is not permitted here.");

        var failure = Assert.Throws<InvalidDataException>(engine.EnsureUnchangedForExecution);
        Assert.Contains("no longer the file that was verified", failure.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(realPath), "The verified binary itself was never touched.");
    }

    [Fact]
    public async Task AModifiedBinaryIsRefusedByVerificationItself()
    {
        var (entry, path) = CreateEngine("original engine"u8.ToArray());
        await File.WriteAllBytesAsync(path, "tampered engine"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => EngineBinaryVerifier.VerifyAsync(_root, entry, CancellationToken.None));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Junctions are removed as links, so a recursive delete does not follow them into the tree
        // they point at.
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                Directory.Delete(directory);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private (EngineManifestEntry Entry, string Path) CreateEngine(byte[] content)
    {
        var relativePath = "restic/0.19.1/win-x64/restic.exe";
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        var entry = new EngineManifestEntry(
            "restic",
            "0.19.1",
            "win-x64",
            relativePath,
            content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)),
            new string('0', 64),
            "https://example.invalid/restic.zip",
            "BSD-2-Clause",
            "0000000");

        return (entry, path);
    }
}
