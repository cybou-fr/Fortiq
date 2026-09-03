using System.Security.Cryptography;
using System.Text.Json;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

/// <summary>
/// The recovery kit is the public half of the recovery promise: it says where the repository is and
/// how it can be unlocked, and it must carry no secret and no unverified content.
/// </summary>
public sealed class RecoveryKitTests : IDisposable
{
    private static readonly RecoveryKitEngine Engine = new("restic", "0.19.1", new string('a', 64));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-kit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AWrittenKitReadsBackAndStillOpensTheSecret()
    {
        var (mnemonic, repositoryId, secret) = Material();
        await WriteKitAsync(repositoryId, mnemonic, secret);

        var opened = await RecoveryKitStore.ReadAsync(_directory, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(repositoryId), opened.Manifest.RepositoryId);
        Assert.Equal("0.19.1", opened.Manifest.Engine.Version);
        var method = Assert.Single(opened.Manifest.UnlockMethods);
        Assert.Equal("bip39", method.ProviderType);

        using var lease = Bip39RecoveryEnvelope.Unwrap(Assert.Single(opened.Envelopes), repositoryId, mnemonic);
        var recovered = new byte[lease.Length];
        lease.CopyTo(recovered);
        Assert.Equal(secret, recovered);
    }

    [Fact]
    public async Task TheKitContainsNoRecoveryMaterial()
    {
        var (mnemonic, repositoryId, secret) = Material();
        await WriteKitAsync(repositoryId, mnemonic, secret);

        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            var content = await File.ReadAllBytesAsync(file);
            var asText = System.Text.Encoding.UTF8.GetString(content);
            Assert.DoesNotContain(mnemonic, asText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexStringLower(secret), Convert.ToHexStringLower(content), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AModifiedEnvelopeIsRefusedByItsHash()
    {
        var (mnemonic, repositoryId, secret) = Material();
        await WriteKitAsync(repositoryId, mnemonic, secret);

        var envelopeFile = Directory.EnumerateFiles(_directory, "*.cbor").Single();
        var bytes = await File.ReadAllBytesAsync(envelopeFile);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(envelopeFile, bytes);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => RecoveryKitStore.ReadAsync(_directory, CancellationToken.None));
        Assert.Contains("does not match the hash", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestThatContradictsItsEnvelopeIsRefused()
    {
        var (mnemonic, repositoryId, secret) = Material();
        await WriteKitAsync(repositoryId, mnemonic, secret);

        // The manifest claims a different repository than the envelope it points at.
        var manifestPath = Path.Combine(_directory, RecoveryKit.ManifestFileName);
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.Replace(Convert.ToHexStringLower(repositoryId), new string('b', 64), StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RecoveryKitStore.ReadAsync(_directory, CancellationToken.None));
    }

    [Fact]
    public async Task AnEntryThatPointsOutsideTheKitIsRefused()
    {
        var (mnemonic, repositoryId, secret) = Material();
        await WriteKitAsync(repositoryId, mnemonic, secret);

        var manifestPath = Path.Combine(_directory, RecoveryKit.ManifestFileName);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var file = document.RootElement.GetProperty("unlockMethods")[0].GetProperty("file").GetString()!;
        await File.WriteAllTextAsync(
            manifestPath,
            (await File.ReadAllTextAsync(manifestPath)).Replace($"\"{file}\"", "\"../outside.cbor\"", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RecoveryKitStore.ReadAsync(_directory, CancellationToken.None));
    }

    [Fact]
    public async Task EnvelopesOfDifferentRepositoriesCannotShareAKit()
    {
        var (mnemonic, repositoryId, secret) = Material();
        using var lease = new BufferKeyLease(secret);
        var other = new byte[32];
        RandomNumberGenerator.Fill(other);

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecoveryKitStore.WriteAsync(
                _directory,
                "C:/repository",
                Engine,
                [
                    Bip39RecoveryEnvelope.Wrap(repositoryId, mnemonic, lease),
                    Bip39RecoveryEnvelope.Wrap(other, mnemonic, lease)
                ],
                clock: null,
                CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static (string Mnemonic, byte[] RepositoryId, byte[] Secret) Material() =>
        (Bip39Mnemonic.Create(), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    private async Task WriteKitAsync(byte[] repositoryId, string mnemonic, byte[] secret)
    {
        using var lease = new BufferKeyLease(secret);
        await RecoveryKitStore.WriteAsync(
            _directory,
            "C:/repository",
            Engine,
            [Bip39RecoveryEnvelope.Wrap(repositoryId, mnemonic, lease)],
            clock: null,
            CancellationToken.None);
    }
}
