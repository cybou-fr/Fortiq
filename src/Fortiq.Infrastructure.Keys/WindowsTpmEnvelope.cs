using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// WindowsTpmEnvelopeV1 of ADR-002: the key that opens the envelope is created inside the TPM through
/// the Microsoft Platform Crypto Provider and cannot be exported. The envelope stores the reference
/// to that key, its public fingerprint and the opaque material it wrapped - never the private key.
/// </summary>
/// <remarks>
/// This is a device-bound convenience path, never a recovery path: a machine that lost its TPM key
/// has lost this envelope, which is why a recovery kit refuses to hold it as the only unlock method.
/// PCR binding is deliberately not used, so a firmware or boot-state change does not silently
/// destroy the daily unlock path.
/// </remarks>
public static class WindowsTpmEnvelope
{
    public const string SuiteId = "windows-tpm-rsa2048-oaep-sha256-hkdf-sha256-aes256gcm-v1";
    public const string ProviderName = "Microsoft Platform Crypto Provider";
    private const string KeyAlgorithm = "RSA-OAEP-SHA256";
    private const int KeySizeInBits = 2048;
    private const int InputKeyMaterialSize = 32;

    private const string ProviderParameter = "provider";
    private const string KeyNameParameter = "keyName";
    private const string AlgorithmParameter = "algorithm";
    private const string FingerprintParameter = "publicKeyFingerprint";
    private const string WrappedMaterialParameter = "wrappedKeyMaterial";

    /// <summary>
    /// Which key store the device key lives in: <c>user</c> or <c>machine</c>. Envelopes written
    /// before this parameter existed carry no scope and are read as user-scoped, which is what they
    /// are.
    /// </summary>
    private const string ScopeParameter = "keyScope";

    private const string UserScope = "user";
    private const string MachineScope = "machine";

    /// <summary>True when this machine exposes the platform provider at all.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            // There is no direct "is this provider present" query, so availability is decided by
            // creating and immediately deleting a throwaway key.
            var probeName = "fortiq-availability-" + Guid.NewGuid().ToString("N");
            try
            {
                using var key = CreateKey(probeName, machineKey: false);
                key.Delete();
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public static KeyEnvelopeV1 Wrap(
        ReadOnlySpan<byte> repositoryId,
        IKeyLease engineUnlockSecret,
        string keyName,
        bool machineKey = false,
        TimeProvider? clock = null)
    {
        RequireWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        using var key = CreateKey(keyName, machineKey);
        if (key.ExportPolicy != CngExportPolicies.None)
        {
            key.Delete();
            throw new CryptographicException("The platform provider produced an exportable key.");
        }

        using var rsa = new RSACng(key);
        var inputKeyMaterial = RandomNumberGenerator.GetBytes(InputKeyMaterialSize);
        try
        {
            var parameters = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ProviderParameter] = Encoding.UTF8.GetBytes(ProviderName),
                [KeyNameParameter] = Encoding.UTF8.GetBytes(keyName),
                [AlgorithmParameter] = Encoding.UTF8.GetBytes(KeyAlgorithm),
                [FingerprintParameter] = Fingerprint(rsa),
                [WrappedMaterialParameter] = rsa.Encrypt(inputKeyMaterial, RSAEncryptionPadding.OaepSHA256),
                // Recorded because it decides who can open the key later. A user-scoped key lives in
                // the creating account's profile, so a Windows service running as a different
                // identity cannot open it however correct the envelope is.
                [ScopeParameter] = Encoding.UTF8.GetBytes(machineKey ? MachineScope : UserScope)
            };

            // The parameters are part of the envelope's authenticated context, so the key reference
            // and the wrapped material cannot be swapped for another key's without the unwrap failing.
            return EnvelopeCipher.Wrap(
                SuiteId,
                EnvelopeProviderType.WindowsTpm,
                inputKeyMaterial,
                repositoryId,
                engineUnlockSecret,
                parameters,
                clock);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputKeyMaterial);
        }
    }

    /// <summary>
    /// Opens the envelope with the TPM key it names. A missing key, a key that is no longer the one
    /// the envelope was created against, or material the TPM refuses to decrypt all end as the same
    /// <see cref="UnlockFailedException"/> as every other unlock failure.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IKeyLease Unwrap(KeyEnvelopeV1 envelope, ReadOnlySpan<byte> repositoryId)
    {
        RequireWindows();
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Suite != SuiteId)
        {
            throw new InvalidDataException("Unsupported envelope suite; this tool cannot open it.");
        }

        var keyName = Text(envelope, KeyNameParameter);
        var provider = Text(envelope, ProviderParameter);
        var scope = OptionalText(envelope, ScopeParameter) ?? UserScope;
        if (Text(envelope, AlgorithmParameter) != KeyAlgorithm)
        {
            throw new InvalidDataException("Unsupported TPM key algorithm in envelope.");
        }

        var inputKeyMaterial = new byte[InputKeyMaterialSize];
        try
        {
            using var key = OpenKey(keyName, provider, scope);
            using var rsa = new RSACng(key);

            // The envelope records which key it was created against, so a different key of the same
            // name - a reinstalled machine, a restored profile - is refused rather than tried.
            if (!CryptographicOperations.FixedTimeEquals(Fingerprint(rsa), Parameter(envelope, FingerprintParameter)))
            {
                throw new UnlockFailedException();
            }

            inputKeyMaterial = rsa.Decrypt(Parameter(envelope, WrappedMaterialParameter), RSAEncryptionPadding.OaepSHA256);
            return EnvelopeCipher.Unwrap(envelope, SuiteId, EnvelopeProviderType.WindowsTpm, inputKeyMaterial, repositoryId);
        }
        catch (CryptographicException)
        {
            throw new UnlockFailedException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputKeyMaterial);
        }
    }

    /// <summary>Removes the TPM key an envelope points at, which revokes that envelope for good.</summary>
    [SupportedOSPlatform("windows")]
    public static void DeleteKey(KeyEnvelopeV1 envelope)
    {
        RequireWindows();
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            using var key = OpenKey(
                Text(envelope, KeyNameParameter),
                Text(envelope, ProviderParameter),
                OptionalText(envelope, ScopeParameter) ?? UserScope);

            key.Delete();
        }
        catch (Exception error) when (error is CryptographicException or UnlockFailedException)
        {
            // A key that is already gone, or one this identity cannot open, leaves the caller in the
            // state they asked for: this envelope no longer opens anything from here.
        }
    }

    [SupportedOSPlatform("windows")]
    private static CngKey CreateKey(string keyName, bool machineKey)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = new CngProvider(ProviderName),
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None
        };

        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(KeySizeInBits), CngPropertyOptions.None));
        return CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
    }

    [SupportedOSPlatform("windows")]
    private static CngKey OpenKey(string keyName, string provider, string scope)
    {
        if (provider != ProviderName)
        {
            throw new InvalidDataException("The envelope names a key storage provider this build does not use.");
        }

        if (scope is not (UserScope or MachineScope))
        {
            throw new InvalidDataException($"The envelope names a key scope this build does not understand: {scope}.");
        }

        try
        {
            // A machine key lives in the machine store and any identity with rights to it can open
            // it; a user key lives in the calling account's profile and nobody else can.
            return CngKey.Open(
                keyName,
                new CngProvider(provider),
                scope == MachineScope ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.UserKey);
        }
        catch (CryptographicException)
        {
            // Windows reports a key that was deleted and a key belonging to another account the
            // same way, so this must not assert which happened. What it can say is the fact the
            // caller could not work out unaided: the key is user-scoped, so it exists only inside
            // one account's profile, and this is the account asking. That is the difference between
            // a fixable configuration and an apparently broken machine - it is also why the message
            // names both possibilities rather than the more dramatic one.
            throw scope == UserScope
                ? new DeviceKeyIdentityException(
                    $"This device key could not be opened. It is user-scoped, so it exists only in the "
                    + $"profile of the account that created it, and this process is running as "
                    + $"'{Identity()}'. Either the key has been removed, or it belongs to another "
                    + "account - a key a Windows service must open has to be created in the machine store.")
                : new UnlockFailedException();
        }
    }

    private static string Identity()
    {
        try
        {
            return Environment.UserName;
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
        {
            return "an unknown account";
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Fingerprint(RSACng rsa) => SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());

    private static byte[] Parameter(KeyEnvelopeV1 envelope, string name) =>
        envelope.ProviderParameters.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException("The envelope is missing a TPM parameter.");

    private static string Text(KeyEnvelopeV1 envelope, string name) => Encoding.UTF8.GetString(Parameter(envelope, name));

    private static string? OptionalText(KeyEnvelopeV1 envelope, string name) =>
        envelope.ProviderParameters.TryGetValue(name, out var value) ? Encoding.UTF8.GetString(value) : null;

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The TPM envelope is available on Windows only.");
        }
    }
}
