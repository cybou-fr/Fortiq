using System.Security.Cryptography;
using System.Text.Json;
using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

public sealed class BundleManifestTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "fortiq-manifest-test-" + Guid.NewGuid().ToString("N"));

    public BundleManifestTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DiscoverManifestFindsManifestInDirectoryOrParent()
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            schema = "fortiq.bundle-manifest",
            version = "1.0.0",
            rid = "win-x64",
            created = DateTimeOffset.UtcNow.ToString("O"),
            components = Array.Empty<object>()
        });

        var rootManifestPath = Path.Combine(_tempDir, "bundle-manifest.json");
        File.WriteAllText(rootManifestPath, manifestJson);

        var subDir = Path.Combine(_tempDir, "desktop");
        Directory.CreateDirectory(subDir);

        // Discover from subfolder
        var (discoveredRoot, manifest) = InstallationManager.DiscoverManifest(subDir);
        Assert.NotNull(discoveredRoot);
        Assert.NotNull(manifest);
        Assert.Equal("fortiq.bundle-manifest", manifest.Schema);
        Assert.Equal("1.0.0", manifest.Version);
    }

    [Fact]
    public void ValidateBundleSucceedsWhenAllComponentsMatchHashes()
    {
        var dummyBytes = "Fortiq Service Binary Content"u8.ToArray();
        var dummyHash = Convert.ToHexStringLower(SHA256.HashData(dummyBytes));

        var svcRelative = "service\\Fortiq.Service.exe";
        var svcFull = Path.Combine(_tempDir, svcRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(svcFull)!);
        File.WriteAllBytes(svcFull, dummyBytes);

        var manifest = new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0.0",
            "win-x64",
            DateTimeOffset.UtcNow.ToString("O"),
            new[]
            {
                new InstallationManager.BundleComponentManifest("Fortiq Service", "service", svcRelative, true, dummyHash)
            });

        // Must succeed without throwing
        InstallationManager.ValidateBundle(_tempDir, manifest);
    }

    [Fact]
    public void ValidateBundleThrowsWhenRequiredComponentIsMissing()
    {
        var manifest = new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0.0",
            "win-x64",
            DateTimeOffset.UtcNow.ToString("O"),
            new[]
            {
                new InstallationManager.BundleComponentManifest("Fortiq Service", "service", "service\\Fortiq.Service.exe", true, new string('0', 64))
            });

        var ex = Assert.Throws<FileNotFoundException>(() =>
            InstallationManager.ValidateBundle(_tempDir, manifest));

        Assert.Contains("Fortiq Service", ex.Message);
        Assert.Contains("required component", ex.Message);
    }

    [Fact]
    public void ValidateBundleThrowsWhenComponentHashMismatches()
    {
        var dummyBytes = "Tampered Binary Content"u8.ToArray();
        var svcRelative = "service\\Fortiq.Service.exe";
        var svcFull = Path.Combine(_tempDir, svcRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(svcFull)!);
        File.WriteAllBytes(svcFull, dummyBytes);

        var manifest = new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0.0",
            "win-x64",
            DateTimeOffset.UtcNow.ToString("O"),
            new[]
            {
                new InstallationManager.BundleComponentManifest("Fortiq Service", "service", svcRelative, true, new string('f', 64))
            });

        var ex = Assert.Throws<InvalidDataException>(() =>
            InstallationManager.ValidateBundle(_tempDir, manifest));

        Assert.Contains("failed SHA-256 validation", ex.Message);
    }
}