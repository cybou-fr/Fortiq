using System.Text;

namespace Fortiq.Infrastructure.Keys;

public enum EnvelopeProviderType
{
    Password,
    Bip39,
    WindowsTpm,
    EnterpriseKms
}

/// <summary>
/// The V1 key envelope of ADR-002: one wrapped Engine Unlock Secret plus the public context it is
/// cryptographically bound to. It never contains the unwrapped secret, the user's password or the
/// recovery material.
/// </summary>
public sealed record KeyEnvelopeV1
{
    public const string Schema = "fortiq.key-envelope";
    public const int SchemaVersion = 1;
    public const string EngineId = "restic";
    public const string EngineUnlockPurpose = "engine-unlock-secret";
    public const int EnvelopeIdSize = 16;
    public const int RepositoryIdSize = 32;

    /// <summary>Bound so a hostile file cannot force an unbounded allocation before validation.</summary>
    public const int MaximumEncodedSize = 64 * 1024;

    public required byte[] EnvelopeId { get; init; }

    public required byte[] RepositoryId { get; init; }

    public required EnvelopeProviderType ProviderType { get; init; }

    public required string Suite { get; init; }

    public required IReadOnlyDictionary<string, byte[]> ProviderParameters { get; init; }

    public required byte[] WrappedSecret { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required IReadOnlyList<string> Critical { get; init; }

    public string Purpose { get; init; } = EngineUnlockPurpose;

    /// <summary>
    /// The HKDF context of ADR-002. Every component is either a fixed-length binary identifier or a
    /// value from a closed ASCII enum, so no two distinct envelopes can produce the same string.
    /// </summary>
    public byte[] DerivationContext() => Encoding.ASCII.GetBytes(
        $"fortiq/v{SchemaVersion}/{ProviderName(ProviderType)}/{Purpose}/{Convert.ToHexStringLower(RepositoryId)}/{Convert.ToHexStringLower(EnvelopeId)}");

    internal static string ProviderName(EnvelopeProviderType providerType) => providerType switch
    {
        EnvelopeProviderType.Password => "password",
        EnvelopeProviderType.Bip39 => "bip39",
        EnvelopeProviderType.WindowsTpm => "windows-tpm",
        EnvelopeProviderType.EnterpriseKms => "enterprise-kms",
        _ => throw new ArgumentOutOfRangeException(nameof(providerType))
    };

    internal static EnvelopeProviderType ParseProvider(string value) => value switch
    {
        "password" => EnvelopeProviderType.Password,
        "bip39" => EnvelopeProviderType.Bip39,
        "windows-tpm" => EnvelopeProviderType.WindowsTpm,
        "enterprise-kms" => EnvelopeProviderType.EnterpriseKms,
        _ => throw new InvalidDataException("Unknown envelope provider type.")
    };
}
