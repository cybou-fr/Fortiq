using System.Security.Cryptography;
using System.Text.Json;

namespace Fortiq.ControlPlane;

/// <summary>
/// Immutable metadata identifying an enrolled endpoint host within a specific tenant fleet.
/// </summary>
public sealed record HostIdentity(
    string TenantId,
    string HostId,
    string Hostname,
    string OsPlatform,
    string AgentVersion,
    string PublicKeyHex,
    DateTimeOffset EnrolledAt)
{
    public string KeyId => DeviceKey.ComputeKeyIdFromPublicKey(PublicKeyHex);
}

/// <summary>
/// Manages NIST P-256 (ecdsa-sha2-nistp256) cryptographic device keys used for signing and verifying
/// control plane telemetry and policies in accordance with ADR-010 and ADR-013.
/// </summary>
public sealed class DeviceKey : IDisposable
{
    public const string Scheme = "ecdsa-sha2-nistp256";

    private readonly ECDsa _ecdsa;
    private readonly bool _ownsEcdsa;
    private bool _disposed;

    private DeviceKey(ECDsa ecdsa, bool ownsEcdsa = true)
    {
        _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        _ownsEcdsa = ownsEcdsa;
    }

    public static DeviceKey Generate()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new DeviceKey(ecdsa);
    }

    public static DeviceKey FromSubjectPublicKeyInfo(ReadOnlySpan<byte> spki)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
        return new DeviceKey(ecdsa);
    }

    public static DeviceKey FromSubjectPublicKeyInfoHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var bytes = Convert.FromHexString(hex);
        return FromSubjectPublicKeyInfo(bytes);
    }

    public static DeviceKey FromPkcs8PrivateKey(ReadOnlySpan<byte> pkcs8)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return new DeviceKey(ecdsa);
    }

    public string ExportPublicKeyHex()
    {
        ThrowIfDisposed();
        var bytes = _ecdsa.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(bytes);
    }

    public byte[] ExportPrivateKeyPkcs8()
    {
        ThrowIfDisposed();
        return _ecdsa.ExportPkcs8PrivateKey();
    }

    public string KeyId
    {
        get
        {
            ThrowIfDisposed();
            var spki = _ecdsa.ExportSubjectPublicKeyInfo();
            return Convert.ToHexStringLower(SHA256.HashData(spki));
        }
    }

    public static string ComputeKeyIdFromPublicKey(string publicKeyHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyHex);
        var bytes = Convert.FromHexString(publicKeyHex);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public string SignHex(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexStringLower(Sign(data));
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        ThrowIfDisposed();
        try
        {
            return _ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool Verify(string publicKeyHex, ReadOnlySpan<byte> data, string signatureHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyHex);
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureHex);

        try
        {
            var spki = Convert.FromHexString(publicKeyHex);
            var signature = Convert.FromHexString(signatureHex);

            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out _);
            return verifier.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsEcdsa)
            {
                _ecdsa.Dispose();
            }
            _disposed = true;
        }
    }
}
