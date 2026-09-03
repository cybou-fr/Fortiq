using System.Security.Cryptography;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

public sealed class EngineManifestTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fortiq-tests-{Guid.NewGuid():N}");

    public EngineManifestTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task VerifierAcceptsExactPinnedBinary()
    {
        var bytes = "verified-engine"u8.ToArray();
        var entry = await CreateEntryAsync(bytes);

        using var verified = await EngineBinaryVerifier.VerifyAsync(_root, entry, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), verified.Sha256);
        Assert.True(Path.IsPathFullyQualified(verified.AbsolutePath));
    }

    [Fact]
    public async Task VerifierRejectsModifiedBinary()
    {
        var entry = await CreateEntryAsync("original"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(_root, entry.RelativePath), "modified"u8.ToArray(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => EngineBinaryVerifier.VerifyAsync(_root, entry with { BinaryLength = 8 }, CancellationToken.None));
    }

    [Fact]
    public async Task ReaderRejectsUnknownFields()
    {
        var path = Path.Combine(_root, "manifest.json");
        await File.WriteAllTextAsync(path, """{"schema":"fortiq.engine-manifest","version":1,"engines":[],"unexpected":true}""", CancellationToken.None);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => EngineManifestReader.ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReaderAcceptsCamelCaseManifest()
    {
        var bytes = "engine"u8.ToArray();
        var entry = await CreateEntryAsync(bytes);
        var path = Path.Combine(_root, "valid.json");
        var json = System.Text.Json.JsonSerializer.Serialize(
            new EngineManifest("fortiq.engine-manifest", 1, [entry]), CamelCaseJson);
        await File.WriteAllTextAsync(path, json, CancellationToken.None);

        var manifest = await EngineManifestReader.ReadAsync(path, CancellationToken.None);

        Assert.Single(manifest.Engines);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<EngineManifestEntry> CreateEntryAsync(byte[] bytes)
    {
        const string relativePath = "restic/0.0.0/win-x64/restic.exe";
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new EngineManifestEntry("restic", "0.0.0", "win-x64", relativePath, bytes.Length, hash, hash, "https://example.invalid/restic.zip", "BSD-2-Clause", "test");
    }
}
