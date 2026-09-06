using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fortiq.Setup;

namespace Fortiq.Desktop.Tests;

public sealed class PackageCacheTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fortiq-cache-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExtractedPackagePassesTheInstallValidatorAndIsReused()
    {
        using var payload = Payload();
        var root = PackageCache.Prepare(payload, _root);
        var (_, manifest) = InstallationManager.DiscoverManifest(Path.Combine(root, "desktop"));
        InstallationManager.ValidateBundle(root, Assert.IsType<InstallationManager.BundleManifest>(manifest));
        Assert.False(File.Exists(Path.Combine(root, ".complete")));
        Assert.True(File.Exists(Path.Combine(_root, "current.complete")));
        payload.Position = 0;
        Assert.Equal(root, PackageCache.Prepare(payload, _root));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("manifest")]
    public void UntrustedCacheIsReplacedWithoutDeletingTheOldCopy(string damage)
    {
        using var payload = Payload();
        var first = PackageCache.Prepare(payload, _root);
        var exe = Path.Combine(first, "desktop", "Fortiq.Desktop.exe");
        if (damage == "changed") File.WriteAllText(exe, "evil");
        if (damage == "missing") File.Delete(exe);
        if (damage == "extra") File.WriteAllText(Path.Combine(first, "injected.dll"), "extra");
        if (damage == "manifest") File.WriteAllText(Path.Combine(first, "bundle-manifest.json"), "{}");
        payload.Position = 0;
        var second = PackageCache.Prepare(payload, _root);
        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(first));
        Assert.Equal("safe", File.ReadAllText(Path.Combine(second, "desktop", "Fortiq.Desktop.exe")));
    }

    [Fact]
    public async Task SetupExtractionThenInstallCopiesTheVerifiedDesktop()
    {
        // Release validation supplies the actual ZIP embedded in Setup. Ordinary unit runs use
        // a small package through the identical extraction and installation path.
        using Stream payload = Environment.GetEnvironmentVariable("FORTIQ_TEST_SETUP_ZIP") is { Length: > 0 } zip
            ? File.OpenRead(zip) : Payload();
        var root = PackageCache.Prepare(payload, Path.Combine(_root, "cache"));
        var target = Path.Combine(_root, "installed");
        await InstallationManager.InstallAsync(new InstallOptions(target,
            InstallService: false, AddToPath: false, SourceDirectory: Path.Combine(root, "desktop"),
            ProvisionAcls: false, AutoStartOnLogon: false, CreateStartMenuShortcut: false));
        using var expected = File.OpenRead(Path.Combine(root, "desktop", "Fortiq.Desktop.exe"));
        using var actual = File.OpenRead(Path.Combine(target, "Fortiq.Desktop.exe"));
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
    }

    [Fact]
    public void TraversalIsRejectedBeforeWritingPayload()
    {
        using var payload = new MemoryStream();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("bad");
        payload.Position = 0;
        Assert.Throws<InvalidDataException>(() => PackageCache.Prepare(payload, _root));
        Assert.False(File.Exists(Path.Combine(_root, "..", "escape.txt")));
    }

    private static MemoryStream Payload()
    {
        var bytes = Encoding.UTF8.GetBytes("safe");
        var manifest = new InstallationManager.BundleManifest("fortiq.bundle-manifest", "1.0.0", "win-x64",
            "0.1.0-beta.1", DateTimeOffset.UtcNow.ToString("O"),
            [new("Desktop", "desktop", "desktop/Fortiq.Desktop.exe", true, Convert.ToHexStringLower(SHA256.HashData(bytes)))],
            [new("desktop/Fortiq.Desktop.exe", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        var payload = new MemoryStream();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, true))
        {
            using (var stream = archive.CreateEntry("desktop/Fortiq.Desktop.exe").Open()) stream.Write(bytes);
            using var writer = new StreamWriter(archive.CreateEntry("bundle-manifest.json").Open());
            writer.Write(JsonSerializer.Serialize(manifest, Options));
        }
        payload.Position = 0;
        return payload;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
