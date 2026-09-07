using System.Text.Json;
using Fortiq.Assistant;

namespace Fortiq.Assistant.Tests;

/// <summary>
/// What the model manifest will and will not accept.
/// </summary>
/// <remarks>
/// The manifest decides which multi-gigabyte binary this product downloads and then runs on somebody
/// else's machine. Everything refused here is refused for the same reason the engine manifest refuses
/// it: by the time the file is on disk it is too late to ask where it came from.
/// </remarks>
public sealed class ModelManifestTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fortiq-model-{Guid.NewGuid():N}");

    public ModelManifestTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AWellFormedManifestIsRead()
    {
        var manifest = await ReadAsync(Valid());

        var entry = Assert.Single(manifest.Models);
        Assert.Equal("fortiq-assistant", entry.Name);
        Assert.Equal(8192, entry.ContextTokens);
    }

    [Fact]
    public async Task AnUnknownFieldIsRefusedRatherThanIgnored()
    {
        var path = Path.Combine(_root, "manifest.json");
        await File.WriteAllTextAsync(
            path,
            """{"schema":"fortiq.model-manifest","version":1,"models":[],"unexpected":true}""",
            CancellationToken.None);

        await Assert.ThrowsAsync<JsonException>(() => ModelManifestReader.ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task AManifestFromSomeOtherSchemaIsRefused()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAsync(Valid() with { Schema = "fortiq.engine-manifest" }));

        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AManifestWithNoModelsIsRefused() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(new ModelManifest("fortiq.model-manifest", 1, [])));

    [Theory]
    [InlineData(@"C:\models\model.gguf")]
    [InlineData("/models/model.gguf")]
    [InlineData("../model.gguf")]
    [InlineData("a/../../model.gguf")]
    public async Task APathThatCanLeaveTheModelFolderIsRefused(string relativePath) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(With(entry => entry with { RelativePath = relativePath })));

    [Fact]
    public async Task AModelWithoutALengthIsRefusedBecauseATruncatedCopyWouldPass() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(With(entry => entry with { FileLength = 0 })));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("ABCD1234ABCD1234ABCD1234ABCD1234ABCD1234ABCD1234ABCD1234ABCD1234")]
    public async Task AHashThatIsNotALowercaseSha256IsRefused(string hash) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(With(entry => entry with { FileSha256 = hash })));

    [Theory]
    [InlineData("http://example.com/model.gguf")]
    [InlineData("file:///C:/model.gguf")]
    [InlineData("not a url")]
    public async Task AModelFetchedOverAnythingButHttpsIsRefused(string url) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(With(entry => entry with { SourceUrl = url })));

    [Fact]
    public async Task AModelWithoutALicenceIsRefused() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(With(entry => entry with { License = "  " })));

    [Fact]
    public async Task AModelWithoutAContextWindowIsRefused() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(With(entry => entry with { ContextTokens = 0 })));

    [Fact]
    public async Task TheSameNameAndVersionTwiceIsRefusedBecauseNeitherEntryWins()
    {
        var entry = Valid().Models[0];

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAsync(new ModelManifest("fortiq.model-manifest", 1, [entry, entry with { RelativePath = "other/model.gguf" }])));
    }

    [Fact]
    public async Task TheManifestShippedInTheRepositoryIsOneThisBuildCanRead()
    {
        // It carries placeholders until a model is pinned, but it must always parse: a manifest that
        // does not is a release whose assistant is dead on arrival, found by whoever installs it.
        var repository = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "manifest.json");
        if (!File.Exists(repository))
        {
            return;
        }

        using var stream = File.OpenRead(repository);
        var manifest = JsonSerializer.Deserialize<ModelManifest>(stream, Strict);

        Assert.NotNull(manifest);
        Assert.Equal("fortiq.model-manifest", manifest.Schema);
        Assert.NotEmpty(manifest.Models);
    }

    private static ModelManifest Valid() => new(
        "fortiq.model-manifest",
        1,
        [
            new ModelManifestEntry(
                "fortiq-assistant",
                "0.1.0",
                "qwen3.5-2b-instruct",
                "Q4_K_M",
                "fortiq-assistant/0.1.0/model.gguf",
                1_234_567_890,
                new string('a', 64),
                "https://example.com/model.gguf",
                "Apache-2.0",
                8192)
        ]);

    private static ModelManifest With(Func<ModelManifestEntry, ModelManifestEntry> change)
    {
        var manifest = Valid();
        return manifest with { Models = [change(manifest.Models[0])] };
    }

    private async Task<ModelManifest> ReadAsync(ModelManifest manifest)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, CamelCase), CancellationToken.None);
        return await ModelManifestReader.ReadAsync(path, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
