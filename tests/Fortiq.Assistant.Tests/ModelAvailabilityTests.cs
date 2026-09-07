using System.Text.Json;
using Fortiq.Assistant;

namespace Fortiq.Assistant.Tests;

/// <summary>
/// Whether this machine has the model, asked before anything tries to use it.
/// </summary>
/// <remarks>
/// Fortiq does not run without its assistant, so the interesting cases are all the ordinary ways a
/// large file fails to arrive: an installation that was interrupted, a folder copied on its own, a
/// download that stopped partway and left something of the right name and the wrong length.
/// </remarks>
public sealed class ModelAvailabilityTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fortiq-model-{Guid.NewGuid():N}");

    public ModelAvailabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AModelThatIsWhereItShouldBeAndTheRightSizeIsUsable()
    {
        await WriteManifestAsync(length: 11);
        await WriteModelAsync("hello world");

        var status = await ModelAvailability.InspectAsync(_root, CancellationToken.None);

        Assert.True(status.Usable);
        Assert.Equal(ModelPresence.Present, status.Presence);
        Assert.Null(status.Detail);
        Assert.NotNull(status.Entry);
    }

    [Fact]
    public async Task AnInstallationWithoutAManifestSaysSoRatherThanFailingLater()
    {
        var status = await ModelAvailability.InspectAsync(_root, CancellationToken.None);

        Assert.False(status.Usable);
        Assert.Equal(ModelPresence.NoManifest, status.Presence);
        Assert.Contains("install the fortiq release again", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AManifestThatIsNotValidJsonIsADamagedCopyAndIsNamedAsOne()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "manifest.json"), "{ not json", CancellationToken.None);

        var status = await ModelAvailability.InspectAsync(_root, CancellationToken.None);

        Assert.Equal(ModelPresence.ManifestUnusable, status.Presence);
        Assert.Contains("damaged", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AManifestNamingAFileThatIsNotThereReportsWhereItLookedFor()
    {
        await WriteManifestAsync(length: 11);

        var status = await ModelAvailability.InspectAsync(_root, CancellationToken.None);

        Assert.Equal(ModelPresence.Missing, status.Presence);
        Assert.Contains(ModelPath(), status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptedDownloadIsAnIncompleteCopyRatherThanAPresentModel()
    {
        // The failure that actually happens after installation: the file exists, and is short.
        await WriteManifestAsync(length: 4_294_967_296);
        await WriteModelAsync("stopped halfway");

        var status = await ModelAvailability.InspectAsync(_root, CancellationToken.None);

        Assert.False(status.Usable);
        Assert.Equal(ModelPresence.Wrong, status.Presence);
        Assert.Contains("incomplete", status.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4294967296", status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectingNothingIsAProgrammingMistakeRatherThanAMissingModel() =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => ModelAvailability.InspectAsync("   ", CancellationToken.None));

    private string ModelPath() => Path.GetFullPath(Path.Combine(_root, "fortiq-assistant", "0.1.0", "model.gguf"));

    private async Task WriteModelAsync(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath())!);
        await File.WriteAllTextAsync(ModelPath(), content, CancellationToken.None);
    }

    private async Task WriteManifestAsync(long length)
    {
        var manifest = new ModelManifest(
            "fortiq.model-manifest",
            1,
            [
                new ModelManifestEntry(
                    "fortiq-assistant",
                    "0.1.0",
                    "qwen3.5-2b-instruct",
                    "Q4_K_M",
                    "fortiq-assistant/0.1.0/model.gguf",
                    length,
                    new string('a', 64),
                    "https://example.com/model.gguf",
                    "Apache-2.0",
                    8192)
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
