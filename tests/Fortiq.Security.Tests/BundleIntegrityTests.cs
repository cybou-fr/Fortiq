using System.Security.Cryptography;
using System.Text;
using Fortiq.Desktop;

namespace Fortiq.Security.Tests;

/// <summary>
/// What the installer will and will not copy onto a machine.
/// </summary>
/// <remarks>
/// The manifest used to hash four executables. A self-contained publish is mostly libraries, and the
/// installer copies those too, so a bundle whose <c>Fortiq.Service.exe</c> hashed correctly and whose
/// <c>Fortiq.Operations.dll</c> had been swapped passed validation - and the service then loaded the
/// swapped library. The EXE was the thing checked; the DLL was the thing that ran.
/// </remarks>
public sealed class BundleIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "fortiq-bundle-tests",
        Guid.NewGuid().ToString("N"));

    public BundleIntegrityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AnIntactBundlePasses()
    {
        var manifest = BuildBundle();
        InstallationManager.ValidateBundle(_root, manifest);
    }

    [Fact]
    public void AReplacedLibraryIsRefusedAlthoughEveryExecutableStillHashesCorrectly()
    {
        var manifest = BuildBundle();

        // The attack the four-executable manifest could not see.
        File.WriteAllText(Path.Combine(_root, "service", "Fortiq.Operations.dll"), "an attacker's library");

        var error = Assert.Throws<InvalidDataException>(() => InstallationManager.ValidateBundle(_root, manifest));
        Assert.Contains("Fortiq.Operations.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplacedExecutableIsStillRefused()
    {
        var manifest = BuildBundle();
        File.WriteAllText(Path.Combine(_root, "service", "Fortiq.Service.exe"), "an attacker's service");

        Assert.Throws<InvalidDataException>(() => InstallationManager.ValidateBundle(_root, manifest));
    }

    [Fact]
    public void ALibraryOfTheWrongLengthIsRefused()
    {
        var manifest = BuildBundle();
        var path = Path.Combine(_root, "service", "Fortiq.Operations.dll");
        File.WriteAllText(path, File.ReadAllText(path) + " ");

        var error = Assert.Throws<InvalidDataException>(() => InstallationManager.ValidateBundle(_root, manifest));
        Assert.Contains("byte(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAddedToTheBundleIsRefusedEvenThoughNothingListedChanged()
    {
        var manifest = BuildBundle();

        // An attacker who cannot alter a hash can still put a library beside one, and .NET assembly
        // probing is happy to find it. Listing what must be there is only half the boundary.
        File.WriteAllText(Path.Combine(_root, "service", "Smuggled.dll"), "loaded by probing");

        var error = Assert.Throws<InvalidDataException>(() => InstallationManager.ValidateBundle(_root, manifest));
        Assert.Contains("not named by the manifest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListedFileThatIsMissingIsRefused()
    {
        var manifest = BuildBundle();
        File.Delete(Path.Combine(_root, "service", "Fortiq.Operations.dll"));

        Assert.Throws<FileNotFoundException>(() => InstallationManager.ValidateBundle(_root, manifest));
    }

    [Fact]
    public void AManifestWithNoFileListIsRefusedRatherThanAcceptedOnTheOldTerms()
    {
        var manifest = BuildBundle() with { Files = null };

        // A bundle predating the file list opts out of the check that is the reason to trust a bundle
        // at all - and an attacker can produce an old-shaped manifest as easily as anyone.
        var error = Assert.Throws<InvalidDataException>(() => InstallationManager.ValidateBundle(_root, manifest));
        Assert.Contains("no file list", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Writes a bundle to disk and returns the manifest describing exactly what was written.</summary>
    private InstallationManager.BundleManifest BuildBundle()
    {
        var payload = new (string Path, string Content)[]
        {
            ("desktop/Fortiq.Desktop.exe", "the desktop"),
            ("desktop/Fortiq.PasswordHelper.exe", "the helper"),
            ("desktop/Avalonia.Base.dll", "a user interface library"),
            ("service/Fortiq.Service.exe", "the service"),
            ("service/Fortiq.Operations.dll", "the library the service loads"),
            ("recover/Fortiq.Recover.exe", "the recovery tool")
        };

        var files = new List<InstallationManager.BundleFileManifest>();
        foreach (var (relative, content) in payload)
        {
            var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(path, bytes);

            files.Add(new(relative, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        string HashOf(string relative) =>
            files.First(file => string.Equals(file.Path, relative, StringComparison.Ordinal)).Sha256;

        return new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0",
            "win-x64",
            null,
            [
                new("desktop", "desktop", "desktop/Fortiq.Desktop.exe", true, HashOf("desktop/Fortiq.Desktop.exe")),
                new("service", "service", "service/Fortiq.Service.exe", true, HashOf("service/Fortiq.Service.exe")),
                new("recover", "recover", "recover/Fortiq.Recover.exe", true, HashOf("recover/Fortiq.Recover.exe")),
                new("passwordHelper", "desktop", "desktop/Fortiq.PasswordHelper.exe", true, HashOf("desktop/Fortiq.PasswordHelper.exe"))
            ],
            files);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
