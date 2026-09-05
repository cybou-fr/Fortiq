using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;

namespace Fortiq.Security.Tests;

/// <summary>
/// The device-bound envelope: it opens without any human secret on the machine that created it, and
/// it opens nowhere else. Every test that touches the TPM cleans its key up again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTpmEnvelopeTests
{
    private static readonly byte[] RepositoryId = [.. Enumerable.Range(0, 32).Select(index => (byte)index)];
    private static readonly byte[] EngineUnlockSecret = [.. Enumerable.Range(0, 32).Select(index => (byte)(index * 7))];

    [SkippableFact]
    public void TheDeviceOpensItsOwnEnvelopeWithoutAnySecret()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out var keyName);
        try
        {
            var reloaded = KeyEnvelopeCodec.Decode(KeyEnvelopeCodec.Encode(envelope));

            using var lease = WindowsTpmEnvelope.Unwrap(reloaded, RepositoryId);
            var recovered = new byte[lease.Length];
            lease.CopyTo(recovered);

            Assert.Equal(EngineUnlockSecret, recovered);
            Assert.Equal(EnvelopeProviderType.WindowsTpm, reloaded.ProviderType);
            Assert.Equal(WindowsTpmEnvelope.SuiteId, reloaded.Suite);
            Assert.Equal(keyName, Encoding.UTF8.GetString(reloaded.ProviderParameters["keyName"]));
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(envelope);
        }
    }

    [SkippableFact]
    public void TheEnvelopeCarriesAReferenceAndWrappedMaterialButNoPrivateKey()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out _);
        try
        {
            var parameters = envelope.ProviderParameters;
            Assert.Equal(WindowsTpmEnvelope.ProviderName, Encoding.UTF8.GetString(parameters["provider"]));
            Assert.Equal("RSA-OAEP-SHA256", Encoding.UTF8.GetString(parameters["algorithm"]));
            Assert.Equal(SHA256.HashSizeInBytes, parameters["publicKeyFingerprint"].Length);
            Assert.Equal(256, parameters["wrappedKeyMaterial"].Length);

            // Nothing in the envelope is the secret, and nothing in it is a private key.
            var encoded = Convert.ToHexStringLower(KeyEnvelopeCodec.Encode(envelope));
            Assert.DoesNotContain(Convert.ToHexStringLower(EngineUnlockSecret), encoded, StringComparison.Ordinal);
            Assert.DoesNotContain("rsa2", encoded, StringComparison.Ordinal);
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(envelope);
        }
    }

    [SkippableFact]
    public void AUserScopedKeyIsRecordedAsSuchSoAReaderNeedNotGuess()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out _);
        try
        {
            // Which key store a device key lives in decides who can open it, and a kit that does not
            // say leaves a service to find out by failing.
            Assert.Equal("user", Encoding.UTF8.GetString(envelope.ProviderParameters["keyScope"]));
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(envelope);
        }
    }

    [SkippableFact]
    [Trait("PrivilegeMode", "Elevated")]
    public void AMachineScopedKeyIsRecordedAndOpensFromTheMachineStore()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(IsElevated, "Creating a key in the machine store requires administrator rights.");

        var keyName = "fortiq-test-machine-" + Guid.NewGuid().ToString("N");
        using var lease = new BufferKeyLease((byte[])EngineUnlockSecret.Clone());
        var envelope = WindowsTpmEnvelope.Wrap(RepositoryId, lease, keyName, machineKey: true);
        try
        {
            Assert.Equal("machine", Encoding.UTF8.GetString(envelope.ProviderParameters["keyScope"]));

            // The scope in the envelope is what the unwrap opens against. A machine key opened as a
            // user key is not found, so this failing would mean the recorded scope is decorative.
            using var opened = WindowsTpmEnvelope.Unwrap(envelope, RepositoryId);
            var recovered = new byte[opened.Length];
            opened.CopyTo(recovered);
            Assert.Equal(EngineUnlockSecret, recovered);
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(envelope);
        }
    }

    /// <summary>Whether this test process can create a key in the machine store.</summary>
    private static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    [SkippableFact]
    public void DeletingTheDeviceKeyRevokesTheEnvelopeForGood()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out _);

        WindowsTpmEnvelope.DeleteKey(envelope);

        // ThrowsAny, because a user-scoped key reports through a derived type that names the scope
        // and the calling account. Windows cannot distinguish a deleted key from one belonging to
        // another identity, so the two arrive here together and both are unlock failures.
        Assert.ThrowsAny<UnlockFailedException>(() => WindowsTpmEnvelope.Unwrap(envelope, RepositoryId));
    }

    [SkippableFact]
    public void AnEnvelopePointedAtADifferentKeyOfTheSameNameIsRefused()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out var keyName);
        WindowsTpmEnvelope.DeleteKey(envelope);

        // The machine was, in effect, reinstalled: the name is taken again by a brand new key.
        var replacement = Wrap(out _, keyName);
        try
        {
            Assert.Throws<UnlockFailedException>(() => WindowsTpmEnvelope.Unwrap(envelope, RepositoryId));
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(replacement);
        }
    }

    [SkippableFact]
    public void AnEnvelopeOfAnotherRepositoryDoesNotOpen()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out _);
        try
        {
            var otherRepository = RepositoryId.ToArray();
            otherRepository[0] ^= 0x01;

            Assert.Throws<UnlockFailedException>(() => WindowsTpmEnvelope.Unwrap(envelope, otherRepository));
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(envelope);
        }
    }

    [SkippableFact]
    public void SwappingTheWrappedMaterialBreaksTheEnvelope()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        var envelope = Wrap(out _);
        var other = Wrap(out _);
        try
        {
            // The provider parameters are authenticated, so material from another envelope cannot be
            // pasted in even though the same TPM could decrypt it.
            var parameters = envelope.ProviderParameters.ToDictionary(StringComparer.Ordinal);
            parameters["wrappedKeyMaterial"] = other.ProviderParameters["wrappedKeyMaterial"];

            Assert.Throws<UnlockFailedException>(
                () => WindowsTpmEnvelope.Unwrap(envelope with { ProviderParameters = parameters }, RepositoryId));
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(envelope);
            WindowsTpmEnvelope.DeleteKey(other);
        }
    }

    [Fact]
    public async Task AKitCannotOfferTheDeviceAsItsOnlyWayBack()
    {
        // The policy is about the kit, not about the hardware, so this uses a device-typed envelope
        // that needs no TPM: the rule has to hold on every machine that can build a kit.
        var directory = Path.Combine(Path.GetTempPath(), "fortiq-tpm-kit-" + Guid.NewGuid().ToString("N"));
        var engine = new RecoveryKitEngine("restic", "0.19.1", new string('a', 64));
        using var lease = new BufferKeyLease(EngineUnlockSecret);
        var deviceEnvelope = EnvelopeCipher.Wrap(
            WindowsTpmEnvelope.SuiteId,
            EnvelopeProviderType.WindowsTpm,
            RandomNumberGenerator.GetBytes(32),
            RepositoryId,
            lease,
            providerParameters: null,
            clock: null);

        try
        {
            var failure = await Assert.ThrowsAsync<ArgumentException>(
                () => RecoveryKitStore.WriteAsync(directory, "C:/repository", engine, [deviceEnvelope], null, CancellationToken.None));
            Assert.Contains("survives the loss of the device", failure.Message, StringComparison.Ordinal);

            // With a recovery method beside it, the same envelope is welcome.
            var kit = await RecoveryKitStore.WriteAsync(
                directory,
                "C:/repository",
                engine,
                [deviceEnvelope, Bip39RecoveryEnvelope.Wrap(RepositoryId, Bip39Mnemonic.Create(), lease)],
                null,
                CancellationToken.None);

            Assert.Equal(2, kit.UnlockMethods.Count);
            Assert.Contains(kit.UnlockMethods, method => method.ProviderType == "windowstpm");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static KeyEnvelopeV1 Wrap(out string keyName, string? existingName = null)
    {
        keyName = existingName ?? "fortiq-test-" + Guid.NewGuid().ToString("N");
        using var lease = new BufferKeyLease(EngineUnlockSecret);
        return WindowsTpmEnvelope.Wrap(RepositoryId, lease, keyName);
    }
}
