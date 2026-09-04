namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Raised when a recovery kit does not describe the thing it is being used on. It is deliberately
/// distinct from an unlock failure: it means the kit and the target disagree, not that the recovery
/// material was wrong.
/// </summary>
public sealed class RecoveryKitMismatchException : Exception
{
    public RecoveryKitMismatchException(string message)
        : base(message)
    {
    }

    public RecoveryKitMismatchException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public RecoveryKitMismatchException()
        : base("The recovery kit does not describe this repository.")
    {
    }
}

/// <summary>How the engine that opens a repository relates to the engine its kit records.</summary>
public enum EngineAgreement
{
    /// <summary>Same engine, same version, same binary.</summary>
    Identical,

    /// <summary>Same engine, a different build or version than the kit was written with.</summary>
    DifferentBuild,

    /// <summary>A different engine entirely; this kit says nothing about how to read that repository.</summary>
    DifferentEngine
}

/// <summary>
/// The relations a kit has to satisfy before it is used. They are stated once, here, rather than
/// spread across the callers that happen to check some of them.
/// </summary>
/// <remarks>
/// <para>kit ↔ envelope: every envelope belongs to the repository the manifest names, matches the
/// hash the manifest records and carries the suite it claims. Enforced when the kit is read.</para>
/// <para>kit ↔ actual repository: the repository states its own identity, and it has to be the one
/// the kit describes. A path is not an identity, so this can only be checked after unlocking.</para>
/// <para>kit ↔ actual engine: the engine name must agree, because a kit written for one engine says
/// nothing about how another one reads a repository. A different build or version of the same engine
/// is allowed and reported, since refusing it would make a kit brittle against an engine upgrade -
/// exactly when recovery matters most.</para>
/// </remarks>
public static class RecoveryKitPolicy
{
    /// <summary>
    /// Confirms the repository that was opened is the one the kit describes. The comparison is on the
    /// identity the repository states about itself, not on the path it was reached through.
    /// </summary>
    public static void RequireSameRepository(RecoveryKit kit, ReadOnlySpan<byte> actualRepositoryId)
    {
        ArgumentNullException.ThrowIfNull(kit);

        if (!string.Equals(kit.RepositoryId, Convert.ToHexStringLower(actualRepositoryId), StringComparison.Ordinal))
        {
            throw new RecoveryKitMismatchException(
                "This recovery kit belongs to a different repository than the one at that location.");
        }
    }

    /// <summary>
    /// Compares the engine in use with the one the kit records, refusing only a different engine.
    /// </summary>
    public static EngineAgreement CompareEngine(RecoveryKit kit, string name, string version, string sha256)
    {
        ArgumentNullException.ThrowIfNull(kit);

        if (!string.Equals(kit.Engine.Name, name, StringComparison.Ordinal))
        {
            throw new RecoveryKitMismatchException(
                "This recovery kit was written for a different repository engine.");
        }

        return string.Equals(kit.Engine.Version, version, StringComparison.Ordinal)
            && string.Equals(kit.Engine.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                ? EngineAgreement.Identical
                : EngineAgreement.DifferentBuild;
    }
}
