using System.Text.Json;

namespace Fortiq.Infrastructure.Updates;

/// <summary>The four roles Fortiq uses. Delegated targets roles are not supported.</summary>
public enum TufRole
{
    Root,
    Targets,
    Snapshot,
    Timestamp
}

/// <summary>Which keys may sign for a role, and how many of them must.</summary>
public sealed record TufRoleTrust(IReadOnlyList<string> KeyIds, int Threshold);

/// <summary>
/// The trusted <c>root</c> document: the keys and thresholds every other role is judged against.
/// </summary>
public sealed class TufRoot
{
    private readonly IReadOnlyDictionary<string, TufKey> _keys;
    private readonly IReadOnlyDictionary<TufRole, TufRoleTrust> _roles;

    private TufRoot(
        long version,
        DateTimeOffset expires,
        IReadOnlyDictionary<string, TufKey> keys,
        IReadOnlyDictionary<TufRole, TufRoleTrust> roles)
    {
        Version = version;
        Expires = expires;
        _keys = keys;
        _roles = roles;
    }

    public long Version { get; }

    public DateTimeOffset Expires { get; }

    public TufRoleTrust TrustFor(TufRole role) => _roles.TryGetValue(role, out var trust)
        ? trust
        : throw new TufMetadataException($"The root document defines no '{Name(role)}' role.");

    /// <summary>Reads the role definitions out of a <c>root</c> document that has already been parsed.</summary>
    public static TufRoot Read(SignedMetadata metadata)
    {
        if (!string.Equals(metadata.Type, "root", StringComparison.Ordinal))
        {
            throw new TufMetadataException($"A document of type '{metadata.Type}' was served as the root role.");
        }

        if (!metadata.Payload.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException("The root document has no 'keys' object.");
        }

        var parsedKeys = new Dictionary<string, TufKey>(StringComparer.Ordinal);
        foreach (var entry in keys.EnumerateObject())
        {
            var key = TufKey.Read(entry.Value);

            // The identifier the document files a key under must be the one the key material produces.
            // Where they differ, a role's threshold could be met by a key nobody meant to trust: the
            // role names an identifier, and the document quietly points that name at other material.
            if (!string.Equals(entry.Name, key.KeyId, StringComparison.Ordinal))
            {
                throw new TufMetadataException(
                    $"The root document files a key under '{entry.Name}', but its material identifies it as '{key.KeyId}'.");
            }

            parsedKeys[key.KeyId] = key;
        }

        if (!metadata.Payload.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException("The root document has no 'roles' object.");
        }

        var parsedRoles = new Dictionary<TufRole, TufRoleTrust>();
        foreach (var role in Enum.GetValues<TufRole>())
        {
            parsedRoles[role] = ReadRole(roles, role, parsedKeys);
        }

        return new TufRoot(metadata.Version, metadata.Expires, parsedKeys, parsedRoles);
    }

    /// <summary>
    /// Counts the distinct trusted keys that signed <paramref name="metadata"/> for <paramref name="role"/>,
    /// and refuses the document when they are fewer than the threshold.
    /// </summary>
    public void RequireSignatures(SignedMetadata metadata, TufRole role)
    {
        var trust = TrustFor(role);
        var satisfied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var signature in metadata.Signatures)
        {
            // Counted only once per key. Without this, one compromised key repeated across the
            // signatures array would meet a threshold of three on its own, which is precisely the
            // guarantee a threshold is there to provide.
            if (satisfied.Contains(signature.KeyId))
            {
                continue;
            }

            if (!trust.KeyIds.Contains(signature.KeyId, StringComparer.Ordinal))
            {
                continue;
            }

            if (_keys.TryGetValue(signature.KeyId, out var key) &&
                key.Verifies(metadata.CanonicalSigned, signature.Signature))
            {
                satisfied.Add(signature.KeyId);
            }
        }

        if (satisfied.Count < trust.Threshold)
        {
            throw new TufMetadataException(
                $"The '{Name(role)}' document carries {satisfied.Count} valid signature(s) from trusted keys, " +
                $"and {trust.Threshold} are required.");
        }
    }

    internal static string Name(TufRole role) => role switch
    {
        TufRole.Root => "root",
        TufRole.Targets => "targets",
        TufRole.Snapshot => "snapshot",
        TufRole.Timestamp => "timestamp",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static TufRoleTrust ReadRole(
        JsonElement roles,
        TufRole role,
        Dictionary<string, TufKey> keys)
    {
        var name = Name(role);
        if (!roles.TryGetProperty(name, out var definition) || definition.ValueKind != JsonValueKind.Object)
        {
            throw new TufMetadataException($"The root document defines no '{name}' role.");
        }

        if (!definition.TryGetProperty("threshold", out var thresholdValue) ||
            thresholdValue.ValueKind != JsonValueKind.Number ||
            !thresholdValue.TryGetInt32(out var threshold) ||
            threshold < 1)
        {
            throw new TufMetadataException($"The '{name}' role has no positive integer 'threshold'.");
        }

        if (!definition.TryGetProperty("keyids", out var keyIds) || keyIds.ValueKind != JsonValueKind.Array)
        {
            throw new TufMetadataException($"The '{name}' role has no 'keyids' array.");
        }

        var parsed = new List<string>();
        foreach (var keyId in keyIds.EnumerateArray())
        {
            if (keyId.ValueKind != JsonValueKind.String)
            {
                throw new TufMetadataException($"The '{name}' role lists a key identifier that is not a string.");
            }

            var value = keyId.GetString()!;
            if (!keys.ContainsKey(value))
            {
                throw new TufMetadataException(
                    $"The '{name}' role trusts key '{value}', which the root document does not define.");
            }

            if (!parsed.Contains(value, StringComparer.Ordinal))
            {
                parsed.Add(value);
            }
        }

        // A role that lists fewer distinct keys than its threshold can never be satisfied. Caught here
        // rather than at the first update, where it would look like a signing failure and send whoever
        // is debugging it after the wrong problem.
        if (parsed.Count < threshold)
        {
            throw new TufMetadataException(
                $"The '{name}' role requires {threshold} signature(s) but lists only {parsed.Count} distinct key(s).");
        }

        return new TufRoleTrust(parsed, threshold);
    }
}
