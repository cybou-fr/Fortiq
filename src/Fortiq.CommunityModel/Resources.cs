namespace Fortiq.CommunityModel;

/// <summary>What kind of data a source names.</summary>
public enum SourceKind
{
    /// <summary>A folder and everything under it.</summary>
    Folder,

    /// <summary>A whole volume, at the filesystem level - not a block image.</summary>
    VolumeFilesystem
}

/// <summary>
/// What Fortiq may read for protection, and nothing else.
/// </summary>
/// <param name="Id">Stable across renames and across being pointed at a different path.</param>
/// <param name="Name">What the person calls it.</param>
/// <param name="Kind">Folder or volume. Not a promise of block semantics.</param>
/// <param name="Path">Where it is on this machine.</param>
/// <remarks>
/// A Source says what data exists and says nothing about where copies of it go, which engine writes
/// them, who can decrypt them or how often any of that happens. That separation is the whole point
/// of this model: today one <c>BackupSchedule</c> record holds the folder, the destination, the
/// recovery kit directory and the recurrence, so protecting the same folder into two places means
/// two records that each redefine the folder, and nothing in the system knows they are the same
/// folder.
/// </remarks>
public sealed record Source(string Id, string Name, SourceKind Kind, string Path);

/// <summary>How the bytes are reached.</summary>
public enum StorageBackend
{
    /// <summary>A path on this machine, an external disk, or a network share.</summary>
    FileSystem,

    /// <summary>An S3-compatible object store.</summary>
    S3,

    /// <summary>SFTP. Not implemented by the current engine wiring; modelled so it can be.</summary>
    Sftp
}

/// <summary>
/// Properties of a storage that a recovery claim may depend on.
/// </summary>
/// <remarks>
/// A flags set of what has actually been established, not what a backend is generally capable of.
/// Spec 24 requires capabilities affecting recovery or ransomware claims to be probed where
/// possible, and the honest consequence is that anything not probed is simply not in the set. An
/// S3 bucket may well have object lock; until something has asked it, Fortiq does not get to say so.
/// </remarks>
[Flags]
public enum StorageCapabilities
{
    None = 0,

    /// <summary>Not on the machine being protected, so it survives that machine.</summary>
    Remote = 1,

    /// <summary>Can be physically disconnected, which is the cheapest defence there is.</summary>
    Removable = 2,

    /// <summary>Keeps previous versions of objects.</summary>
    Versioned = 4,

    /// <summary>Refuses overwrites and deletions for a retention period.</summary>
    Immutable = 8,

    /// <summary>Object lock is configured, not merely available on this backend.</summary>
    ObjectLock = 16,

    /// <summary>Bytes are encrypted in transit by the transport itself.</summary>
    EncryptedTransport = 32,

    /// <summary>Reachable without the endpoint that wrote to it - a disk somebody can carry.</summary>
    IndependentOfEndpoint = 64,

    /// <summary>Reachable right now. The only capability that is a fact about this moment.</summary>
    CurrentlyAvailable = 128
}

/// <summary>
/// A place repositories may live.
/// </summary>
/// <param name="Id">Stable identifier for this place.</param>
/// <param name="Name">What the person calls it.</param>
/// <param name="Backend">How it is reached.</param>
/// <param name="Location">The locator: a path, or an endpoint and container.</param>
/// <param name="CredentialRef">Which credential opens it, when one is needed.</param>
/// <param name="Capabilities">What has been established about it. See <see cref="StorageCapabilities"/>.</param>
/// <remarks>
/// A Storage is not a Repository. Several repositories may sit in one storage, and Spec 24 is
/// explicit that Repository must not become the user-facing word for Storage - which is exactly the
/// confusion the current model invites, since a schedule's "repository location" is both at once.
/// </remarks>
public sealed record Storage(
    string Id,
    string Name,
    StorageBackend Backend,
    string Location,
    string? CredentialRef = null,
    StorageCapabilities Capabilities = StorageCapabilities.None);

/// <summary>What sort of secret a credential is, so the model can name one without holding it.</summary>
public enum StorageCredentialKind
{
    /// <summary>An access key and secret for an object store.</summary>
    ObjectStorageKey,

    /// <summary>An SSH key or agent reference.</summary>
    SshKey,

    /// <summary>No credential: the filesystem's own permissions decide.</summary>
    None
}

/// <summary>
/// A pointer to storage access material. Never the material.
/// </summary>
/// <remarks>
/// Spec 24 keeps two questions apart that are easy to blur: storage access asks whether a process
/// can reach the backup bytes, and identity asks whether a principal can decrypt them. Fortiq's
/// encryption does not care which of the two an attacker has, but a person configuring backups
/// cares enormously, and a model that stored both in one place would make it impossible to say
/// "these three tasks share one MinIO login" without also saying they share a recovery key.
///
/// Nothing here carries a secret. The reference resolves against the platform's own store.
/// </remarks>
public sealed record StorageCredentialRef(string Id, string Name, StorageCredentialKind Kind);

/// <summary>
/// Which repository format is written, named rather than assumed.
/// </summary>
/// <remarks>
/// Community has exactly one engine, and modelling it anyway is not ceremony: a Recovery Kit has to
/// say what will open the archive it describes, and "whatever Fortiq shipped that year" is not an
/// answer somebody can act on in five years' time with a disk and no Fortiq.
/// </remarks>
public sealed record RepositoryEngineRef(string Id, string Name, string Version);

/// <summary>What sort of principal an identity is.</summary>
public enum IdentityKind
{
    /// <summary>A person.</summary>
    Person,

    /// <summary>A machine, holding a key bound to its hardware.</summary>
    Device,

    /// <summary>Words on paper in a drawer, which is a principal like any other.</summary>
    PaperRecovery,

    /// <summary>Something else that holds a key.</summary>
    Other
}

/// <summary>
/// Someone or something that may hold a key - not a Windows account and not a storage login.
/// </summary>
public sealed record Identity(string Id, string Name, IdentityKind Kind);

/// <summary>How a key is held.</summary>
public enum IdentityKeyKind
{
    /// <summary>A BIP-39 recovery phrase, held by whoever wrote it down.</summary>
    RecoveryPhrase,

    /// <summary>A key sealed to this machine's hardware, usable only on it.</summary>
    DeviceBound,

    /// <summary>Something else.</summary>
    Other
}

/// <summary>
/// Metadata about one key an identity holds. Never the key.
/// </summary>
/// <param name="Id">Stable identifier, referenced by encryption profiles.</param>
/// <param name="IdentityId">Whose key it is.</param>
/// <param name="Kind">How it is held.</param>
/// <param name="Description">What somebody needs to know to find or use it.</param>
/// <param name="Confirmed">
/// Whether the holder has demonstrated they have it. A recovery phrase that was generated and never
/// written down is not a recovery route, and this is the field that stops the model claiming it is.
/// </param>
public sealed record IdentityKey(
    string Id,
    string IdentityId,
    IdentityKeyKind Kind,
    string Description,
    bool Confirmed = false);

/// <summary>
/// Who can decrypt, and what can unlock for unattended writes.
/// </summary>
/// <param name="Recipients">Principals able to recover. These are the people who get the data back.</param>
/// <param name="Writers">
/// Principals able to unlock for recurring writes without anybody present. A device-bound key is
/// the usual one, and it is deliberately not a recipient: a machine that is stolen or wiped must not
/// be the only thing that could have opened the backups.
/// </param>
/// <remarks>
/// Reusable, and referenced by routes rather than restated per repository, so "who can get this
/// back" becomes a question with one answer per policy instead of one answer per archive.
/// </remarks>
public sealed record EncryptionProfile(
    string Id,
    string Name,
    IReadOnlyList<string> Recipients,
    IReadOnlyList<string> Writers);
