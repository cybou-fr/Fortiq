using System.Collections.ObjectModel;

namespace Fortiq.CommunityModel;

/// <summary>
/// Everything this machine has declared, as one readable whole.
/// </summary>
/// <remarks>
/// A catalogue rather than a graph of object references. Resources point at each other by
/// identifier, which is what lets the same shape be a file on disk, a projection of the old
/// schedules, a draft a person is editing and something an assistant proposed - without any of them
/// having to be loaded in a particular order, or being able to form a cycle nobody can serialise.
///
/// Resolution is therefore a lookup, and the lookups here return null rather than throwing. A
/// catalogue projected from an incomplete machine is a normal thing to be holding, and the caller
/// that finds a route with no storage should be able to say so on screen instead of crashing.
/// </remarks>
public sealed record ResourceCatalog(
    IReadOnlyList<Source> Sources,
    IReadOnlyList<Storage> Storages,
    IReadOnlyList<StorageCredentialRef> Credentials,
    IReadOnlyList<RepositoryEngineRef> Engines,
    IReadOnlyList<Identity> Identities,
    IReadOnlyList<IdentityKey> IdentityKeys,
    IReadOnlyList<EncryptionProfile> EncryptionProfiles,
    IReadOnlyList<BackupRoute> Routes,
    IReadOnlyList<BackupTask> Tasks)
{
    /// <summary>A catalogue for a machine that has declared nothing.</summary>
    public static ResourceCatalog Empty { get; } = new([], [], [], [], [], [], [], [], []);

    public Source? Source(string id) => Find(Sources, id, source => source.Id);

    public Storage? Storage(string id) => Find(Storages, id, storage => storage.Id);

    public StorageCredentialRef? Credential(string id) => Find(Credentials, id, credential => credential.Id);

    public RepositoryEngineRef? Engine(string id) => Find(Engines, id, engine => engine.Id);

    public Identity? Identity(string id) => Find(Identities, id, identity => identity.Id);

    public IdentityKey? IdentityKey(string id) => Find(IdentityKeys, id, key => key.Id);

    public EncryptionProfile? EncryptionProfile(string id) => Find(EncryptionProfiles, id, profile => profile.Id);

    public BackupRoute? Route(string id) => Find(Routes, id, route => route.Id);

    public BackupTask? Task(string id) => Find(Tasks, id, task => task.Id);

    /// <summary>The routes a task produces, skipping any the catalogue cannot resolve.</summary>
    public IReadOnlyList<BackupRoute> RoutesOf(BackupTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ReadOnlyCollection<BackupRoute>(
            task.RouteIds.Select(Route).OfType<BackupRoute>().ToList());
    }

    /// <summary>The sources a task protects, skipping any the catalogue cannot resolve.</summary>
    public IReadOnlyList<Source> SourcesOf(BackupTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ReadOnlyCollection<Source>(
            task.SourceIds.Select(Source).OfType<Source>().ToList());
    }

    /// <summary>The keys that can decrypt what a profile protects.</summary>
    public IReadOnlyList<IdentityKey> RecipientsOf(EncryptionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ReadOnlyCollection<IdentityKey>(
            profile.Recipients.Select(IdentityKey).OfType<IdentityKey>().ToList());
    }

    /// <summary>
    /// Whether anybody has actually proved they hold a key that could recover this profile.
    /// </summary>
    /// <remarks>
    /// The distinction the health model already makes, expressed in the resource model too: a
    /// recovery phrase that was generated and never confirmed is not a recovery route, and a profile
    /// whose only recipient is unconfirmed protects data nobody has demonstrated they can get back.
    /// </remarks>
    public bool HasConfirmedRecipient(EncryptionProfile profile) =>
        RecipientsOf(profile).Any(key => key.Confirmed);

    /// <summary>
    /// Finds one resource by identifier, or nothing.
    /// </summary>
    /// <remarks>
    /// A blank identifier resolves to nothing rather than throwing. It used to throw, which was the
    /// wrong judgement: "this proposal names no encryption profile" is a question validation has to
    /// be able to ask, and the answer is that no such profile exists - not that the caller made a
    /// programming mistake. A proposal with a field left empty is an ordinary thing to be holding.
    /// </remarks>
    private static T? Find<T>(IReadOnlyList<T> items, string id, Func<T, string> identify) where T : class =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : items.FirstOrDefault(item => string.Equals(identify(item), id, StringComparison.Ordinal));
}
