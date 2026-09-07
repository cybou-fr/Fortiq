using System.Text.Json;
using Fortiq.Assistant;

namespace Fortiq.Assistant.Tests;

/// <summary>
/// Whether this machine has something to run the model on, asked before anything tries.
/// </summary>
/// <remarks>
/// The model and the runtime go missing independently and are fixed independently, so they are
/// asked about separately and reported separately. One message covering both would send somebody to
/// reinstall the half they already have.
/// </remarks>
public sealed class RuntimeAvailabilityTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fortiq-runtime-{Guid.NewGuid():N}");

    public RuntimeAvailabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ARuntimeThatIsWhereItShouldBeIsUsable()
    {
        await WriteManifestAsync();
        WriteServer();

        var status = await RuntimeAvailability.InspectAsync(_root, "win-x64", CancellationToken.None);

        Assert.True(status.Usable);
        Assert.Equal("b10830", status.Entry!.Version);
    }

    [Fact]
    public async Task AnInstallationWithoutAManifestSaysSo()
    {
        var status = await RuntimeAvailability.InspectAsync(_root, "win-x64", CancellationToken.None);

        Assert.Equal(ModelPresence.NoManifest, status.Presence);
        Assert.Contains("install the fortiq release again", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AManifestNamingAServerThatIsNotThereReportsWhereItLooked()
    {
        await WriteManifestAsync();

        var status = await RuntimeAvailability.InspectAsync(_root, "win-x64", CancellationToken.None);

        Assert.Equal(ModelPresence.Missing, status.Presence);
        Assert.Contains(ServerPath(), status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlatformThisBuildHasNoRuntimeForIsSaidPlainly()
    {
        await WriteManifestAsync();
        WriteServer();

        var status = await RuntimeAvailability.InspectAsync(_root, "linux-arm64", CancellationToken.None);

        Assert.False(status.Usable);
        Assert.Contains("linux-arm64", status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestThatIsNotValidJsonIsADamagedCopy()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "manifest.json"), "{ not json", CancellationToken.None);

        var status = await RuntimeAvailability.InspectAsync(_root, "win-x64", CancellationToken.None);

        Assert.Equal(ModelPresence.ManifestUnusable, status.Presence);
        Assert.Contains("damaged", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APathThatLeavesTheRuntimeFolderIsRefused()
    {
        await WriteManifestAsync(relativePath: "../elsewhere/llama-server.exe");

        var status = await RuntimeAvailability.InspectAsync(_root, "win-x64", CancellationToken.None);

        Assert.Equal(ModelPresence.ManifestUnusable, status.Presence);
    }

    [Fact]
    public async Task ASourceThatIsNotHttpsIsRefused()
    {
        await WriteManifestAsync(sourceUrl: "http://example.com/llama.zip");

        var status = await RuntimeAvailability.InspectAsync(_root, "win-x64", CancellationToken.None);

        Assert.Equal(ModelPresence.ManifestUnusable, status.Presence);
    }

    [Fact]
    public async Task TheManifestShippedInTheRepositoryDescribesARuntimeThisBuildCanUse()
    {
        // Placeholders here would mean a release whose assistant never works, found by its users.
        var repository = Repository();
        if (repository is null)
        {
            return;
        }

        var status = await RuntimeAvailability.InspectAsync(repository, "win-x64", CancellationToken.None);

        Assert.NotEqual(ModelPresence.NoManifest, status.Presence);
        Assert.NotEqual(ModelPresence.ManifestUnusable, status.Presence);
        Assert.Equal("MIT", status.Entry!.License);
    }

    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "runtimes");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private string ServerPath() => Path.GetFullPath(Path.Combine(_root, "llama", "b10830", "win-x64", "llama-server.exe"));

    private void WriteServer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServerPath())!);
        File.WriteAllText(ServerPath(), "not really an executable");
    }

    private async Task WriteManifestAsync(
        string relativePath = "llama/b10830/win-x64/llama-server.exe",
        string sourceUrl = "https://example.com/llama.zip")
    {
        var manifest = new RuntimeManifest(
            "fortiq.runtime-manifest",
            1,
            [
                new RuntimeManifestEntry(
                    "llama-server",
                    "b10830",
                    "win-x64",
                    relativePath,
                    new string('a', 64),
                    18_416_266,
                    sourceUrl,
                    "MIT")
            ]);

        await File.WriteAllTextAsync(
            Path.Combine(_root, "manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCase),
            CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
