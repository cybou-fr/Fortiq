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
            },
            // Every installed file, not only the executable the component is named for. A bundle whose
            // EXE hashes and whose libraries do not is one the installer used to accept.
            new[]
            {
                new InstallationManager.BundleFileManifest("service/Fortiq.Service.exe", dummyBytes.Length, dummyHash)
            });

        // Must succeed without throwing
        InstallationManager.ValidateBundle(_tempDir, manifest);
    }

    [Fact]
    public void ValidateBundleSucceedsWithReadmeFirstInPayload()
    {
        var dummyBytes = "Fortiq Service Binary Content"u8.ToArray();
        var dummyHash = Convert.ToHexStringLower(SHA256.HashData(dummyBytes));
        var readmeBytes = "Welcome to Fortiq Community Edition"u8.ToArray();
        var readmeHash = Convert.ToHexStringLower(SHA256.HashData(readmeBytes));

        var svcRelative = "service\\Fortiq.Service.exe";
        var svcFull = Path.Combine(_tempDir, svcRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(svcFull)!);
        File.WriteAllBytes(svcFull, dummyBytes);

        var readmeFull = Path.Combine(_tempDir, "README-FIRST.txt");
        File.WriteAllBytes(readmeFull, readmeBytes);

        var manifest = new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0.0",
            "win-x64",
            DateTimeOffset.UtcNow.ToString("O"),
            new[]
            {
                new InstallationManager.BundleComponentManifest("Fortiq Service", "service", svcRelative, true, dummyHash)
            },
            new[]
            {
                new InstallationManager.BundleFileManifest("service/Fortiq.Service.exe", dummyBytes.Length, dummyHash),
                new InstallationManager.BundleFileManifest("README-FIRST.txt", readmeBytes.Length, readmeHash)
            });

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
            },
            new[]
            {
                new InstallationManager.BundleFileManifest("service/Fortiq.Service.exe", dummyBytes.Length, new string('f', 64))
            });

        var ex = Assert.Throws<InvalidDataException>(() =>
            InstallationManager.ValidateBundle(_tempDir, manifest));

        Assert.Contains("failed SHA-256 validation", ex.Message);
    }

    [Fact]
    public void DiscoverManifestResolvesParentWhenRunningFromChildFolder()
    {
        var desktopDir = Path.Combine(_tempDir, "desktop");
        var serviceDir = Path.Combine(_tempDir, "service");
        Directory.CreateDirectory(desktopDir);
        Directory.CreateDirectory(serviceDir);

        var desktopExe = Path.Combine(desktopDir, "Fortiq.Desktop.exe");
        var serviceExe = Path.Combine(serviceDir, "Fortiq.Service.exe");
        File.WriteAllBytes(desktopExe, "desktop"u8.ToArray());
        File.WriteAllBytes(serviceExe, "service"u8.ToArray());

        var manifestJson = JsonSerializer.Serialize(new
        {
            schema = "fortiq.bundle-manifest",
            version = "1.0.0",
            rid = "win-x64",
            created = DateTimeOffset.UtcNow.ToString("O"),
            components = new[]
            {
                new
                {
                    name = "desktop",
                    folder = "desktop",
                    mainExecutable = "desktop/Fortiq.Desktop.exe",
                    required = true,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData("desktop"u8.ToArray()))
                },
                new
                {
                    name = "service",
                    folder = "service",
                    mainExecutable = "service/Fortiq.Service.exe",
                    required = true,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData("service"u8.ToArray()))
                }
            }
        });

        File.WriteAllText(Path.Combine(_tempDir, "bundle-manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(desktopDir, "bundle-manifest.json"), manifestJson);

        // When queried from desktop subfolder, it must resolve to _tempDir (the bundle root)
        var (discoveredRoot, manifest) = InstallationManager.DiscoverManifest(desktopDir);
        Assert.NotNull(discoveredRoot);
        Assert.NotNull(manifest);
        Assert.Equal(Path.GetFullPath(_tempDir), Path.GetFullPath(discoveredRoot));
        Assert.Equal(2, manifest.Components.Count);
    }

    [Fact]
    public async Task InstallAsyncWithBundleDeploysAllComponentsAndManifest()
    {
        var desktopDir = Path.Combine(_tempDir, "bundle", "desktop");
        var serviceDir = Path.Combine(_tempDir, "bundle", "service");
        Directory.CreateDirectory(desktopDir);
        Directory.CreateDirectory(serviceDir);

        var desktopExe = Path.Combine(desktopDir, "Fortiq.Desktop.exe");
        var serviceExe = Path.Combine(serviceDir, "Fortiq.Service.exe");
        var desktopBytes = "desktop-binary"u8.ToArray();
        var serviceBytes = "service-binary"u8.ToArray();
        File.WriteAllBytes(desktopExe, desktopBytes);
        File.WriteAllBytes(serviceExe, serviceBytes);

        var bundleRoot = Path.Combine(_tempDir, "bundle");
        var manifestJson = JsonSerializer.Serialize(new
        {
            schema = "fortiq.bundle-manifest",
            version = "1.0.0",
            rid = "win-x64",
            created = DateTimeOffset.UtcNow.ToString("O"),
            components = new[]
            {
                new
                {
                    name = "desktop",
                    folder = "desktop",
                    mainExecutable = "desktop/Fortiq.Desktop.exe",
                    required = true,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(desktopBytes))
                },
                new
                {
                    name = "service",
                    folder = "service",
                    mainExecutable = "service/Fortiq.Service.exe",
                    required = true,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(serviceBytes))
                }
            },
            // The whole payload, which is what the installer now verifies. Listing only the two
            // executables would leave every library in the bundle outside the integrity boundary.
            files = new[]
            {
                new
                {
                    path = "desktop/Fortiq.Desktop.exe",
                    length = (long)desktopBytes.Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(desktopBytes))
                },
                new
                {
                    path = "service/Fortiq.Service.exe",
                    length = (long)serviceBytes.Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(serviceBytes))
                }
            }
        });
        File.WriteAllText(Path.Combine(bundleRoot, "bundle-manifest.json"), manifestJson);

        var targetDir = Path.Combine(_tempDir, "installed");
        var options = new InstallOptions(
            targetDir,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: desktopDir,
            ProvisionAcls: false);

        await InstallationManager.InstallAsync(options);

        Assert.True(File.Exists(Path.Combine(targetDir, "Fortiq.Desktop.exe")));
        Assert.True(File.Exists(Path.Combine(targetDir, "Fortiq.Service.exe")));
        Assert.True(File.Exists(Path.Combine(targetDir, "bundle-manifest.json")));
    }
}