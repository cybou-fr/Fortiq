using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Platform.Windows;

namespace Fortiq.Infrastructure.ObjectStorage;

/// <summary>
/// Supplies storage credentials held per repository, encrypted to this machine.
/// </summary>
/// <remarks>
/// The environment provider it replaces has four problems that only look small until a service is
/// involved. A Windows service does not inherit the environment somebody typed the keys into. One
/// set of keys serves every repository, so a credential that only needs to write to one bucket
/// reaches all of them. Rotation means editing machine environment variables and restarting. And
/// the secret sits in a place readable by anything that can enumerate the process environment.
/// <para>
/// Credentials are stored against the repository location, which is what makes per-repository
/// identities possible: the write-only identity for one bucket cannot be used against another.
/// Whether an operator actually issues one identity per repository is their decision, but nothing
/// here forces them to share one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StoredObjectStorageCredentials : IObjectStorageCredentialProvider
{
    private readonly MachineCredentialStore _store;

    public StoredObjectStorageCredentials(MachineCredentialStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public StoredObjectStorageCredentials(string directory)
        : this(new MachineCredentialStore(directory))
    {
    }

    public async Task<ObjectStorageCredentials?> ForRepositoryAsync(
        string repositoryLocation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);

        // A repository in a local directory needs no storage identity, and asking for one would
        // invite somebody to configure a credential that is never used.
        if (!RepositoryLocation.IsObjectStorage(repositoryLocation))
        {
            return null;
        }

        var stored = await _store.ReadAsync(Subject(repositoryLocation), cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var document = JsonNode.Parse(stored)
            ?? throw new InvalidDataException("The stored storage credential is empty.");

        return new ObjectStorageCredentials(
            document["accessKeyId"]?.GetValue<string>()
                ?? throw new InvalidDataException("The stored storage credential has no access key."),
            document["secretAccessKey"]?.GetValue<string>()
                ?? throw new InvalidDataException("The stored storage credential has no secret key."),
            document["region"]?.GetValue<string>());
    }

    /// <summary>Stores the credentials for one repository, replacing whatever was there.</summary>
    public Task WriteAsync(
        string repositoryLocation,
        ObjectStorageCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!RepositoryLocation.IsObjectStorage(repositoryLocation))
        {
            throw new ArgumentException(
                "Only a repository in object storage uses storage credentials.",
                nameof(repositoryLocation));
        }

        var document = new JsonObject
        {
            ["accessKeyId"] = credentials.AccessKeyId,
            ["secretAccessKey"] = credentials.SecretAccessKey,
            ["region"] = credentials.Region
        };

        return _store.WriteAsync(
            Subject(repositoryLocation),
            document.ToJsonString(new JsonSerializerOptions()),
            cancellationToken);
    }

    /// <summary>Forgets the credentials for one repository. Returns whether there were any.</summary>
    public bool Remove(string repositoryLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);
        return _store.Remove(Subject(repositoryLocation));
    }

    /// <summary>
    /// Normalised so that the same repository written two ways resolves to one credential, and two
    /// different repositories never share one by accident.
    /// </summary>
    private static string Subject(string repositoryLocation) =>
        "storage:" + RepositoryLocation.Normalize(repositoryLocation);
}

/// <summary>
/// Asks each provider in turn and takes the first answer.
/// </summary>
/// <remarks>
/// The order is the policy. Credentials stored for a specific repository are more specific than one
/// set of environment variables covering every repository on the machine, so they win; the
/// environment remains as a fallback for tooling, tests and CI, where exporting two variables is the
/// natural thing and a machine store would be in the way.
/// </remarks>
public sealed class FirstAvailableObjectStorageCredentials : IObjectStorageCredentialProvider
{
    private readonly IReadOnlyList<IObjectStorageCredentialProvider> _providers;

    public FirstAvailableObjectStorageCredentials(params IObjectStorageCredentialProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.Length > 0
            ? providers
            : throw new ArgumentException("At least one provider is required.", nameof(providers));
    }

    public async Task<ObjectStorageCredentials?> ForRepositoryAsync(
        string repositoryLocation,
        CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (await provider.ForRepositoryAsync(repositoryLocation, cancellationToken) is { } credentials)
            {
                return credentials;
            }
        }

        return null;
    }
}
