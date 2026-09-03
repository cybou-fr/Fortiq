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
                [WrappedMaterialParameter] = rsa.Encrypt(inputKeyMaterial, RSAEncryptionPadding.OaepSHA256)
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
        if (Text(envelope, AlgorithmParameter) != KeyAlgorithm)
        {
            throw new InvalidDataException("Unsupported TPM key algorithm in envelope.");
        }

        var inputKeyMaterial = new byte[InputKeyMaterialSize];
        try
        {
            using var key = OpenKey(keyName, provider);
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
            using var key = OpenKey(Text(envelope, KeyNameParameter), Text(envelope, ProviderParameter));
            key.Delete();
        }
        catch (CryptographicException)
        {
            // A key that is already gone is the state the caller asked for.
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
    private static CngKey OpenKey(string keyName, string provider)
    {
        if (provider != ProviderName)
        {
            throw new InvalidDataException("The envelope names a key storage provider this build does not use.");
        }

        try
        {
            return CngKey.Open(keyName, new CngProvider(provider));
        }
        catch (CryptographicException)
        {
            // The device no longer holds this key, which is one more way for an unlock to fail.
            throw new UnlockFailedException();
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Fingerprint(RSACng rsa) => SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());

    private static byte[] Parameter(KeyEnvelopeV1 envelope, string name) =>
        envelope.ProviderParameters.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException("The envelope is missing a TPM parameter.");

    private static string Text(KeyEnvelopeV1 envelope, string name) => Encoding.UTF8.GetString(Parameter(envelope, name));

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The TPM envelope is available on Windows only.");
        }
    }
}
