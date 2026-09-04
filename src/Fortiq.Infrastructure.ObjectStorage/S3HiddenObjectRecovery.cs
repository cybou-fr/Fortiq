using Amazon.S3;
using Amazon.S3.Model;
using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Infrastructure.ObjectStorage;

/// <summary>
/// Brings back objects that were hidden behind delete markers in a versioned bucket.
/// </summary>
/// <remarks>
/// <para>It only ever deletes delete markers, which carry no data, and only within the repository's
/// own prefix. A version holding data is never touched: in a locked bucket the storage would refuse
/// anyway, and in an unlocked one refusing here is the difference between recovering a repository
/// and finishing the job an attacker started.</para>
/// <para>Locks are left alone. The engine creates a lock object for each operation and deletes it
/// when it finishes, so in a versioned bucket a healthy repository always carries delete markers
/// over locks it removed on purpose. Bringing those back would re-lock a repository that nothing is
/// working on - the opposite of a recovery.</para>
/// </remarks>
public sealed class S3HiddenObjectRecovery : IHiddenObjectRecovery
{
    private readonly IObjectStorageCredentialProvider _credentials;

    public S3HiddenObjectRecovery(IObjectStorageCredentialProvider credentials) =>
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public async Task<HiddenObjects> InspectAsync(string repositoryLocation, CancellationToken cancellationToken)
    {
        var (client, address) = await ConnectAsync(repositoryLocation, cancellationToken);
        using (client)
        {
            var versioning = await client.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = address.Bucket },
                cancellationToken);

            var markers = await HiddenKeysAsync(client, address, cancellationToken);
            return new HiddenObjects(
                markers.Count,
                markers.Count(entry => entry.Value),
                versioning.VersioningConfig?.Status == VersionStatus.Enabled);
        }
    }

    public async Task<HiddenObjectsRestored> RestoreAsync(string repositoryLocation, CancellationToken cancellationToken)
    {
        var (client, address) = await ConnectAsync(repositoryLocation, cancellationToken);
        using (client)
        {
            var hidden = await HiddenKeysAsync(client, address, cancellationToken);
            var restored = 0;

            foreach (var (key, recoverable) in hidden)
            {
                if (!recoverable)
                {
                    // Nothing survives underneath this marker, so removing it would only turn a
                    // hidden object into a missing one.
                    continue;
                }

                foreach (var marker in await MarkersForAsync(client, address, key, cancellationToken))
                {
                    await client.DeleteObjectAsync(
                        new DeleteObjectRequest
                        {
                            BucketName = address.Bucket,
                            Key = key,
                            VersionId = marker
                        },
                        cancellationToken);
                }

                restored++;
            }

            var remaining = await HiddenKeysAsync(client, address, cancellationToken);
            return new HiddenObjectsRestored(restored, remaining.Count);
        }
    }

    /// <summary>
    /// Every key whose newest version is a delete marker, and whether a version with data survives
    /// beneath it.
    /// </summary>
    private static async Task<Dictionary<string, bool>> HiddenKeysAsync(
        IAmazonS3 client,
        ObjectStorageAddress address,
        CancellationToken cancellationToken)
    {
        var hidden = new Dictionary<string, bool>(StringComparer.Ordinal);
        var withData = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var version in AllVersionsAsync(client, address, cancellationToken))
        {
            if (!IsRecoverableContent(version.Key, address.Prefix))
            {
                continue;
            }

            if (version.IsDeleteMarker == true)
            {
                if (version.IsLatest == true)
                {
                    hidden[version.Key] = false;
                }

                continue;
            }

            withData.Add(version.Key);
        }

        foreach (var key in hidden.Keys.ToArray())
        {
            hidden[key] = withData.Contains(key);
        }

        return hidden;
    }

    /// <summary>
    /// Whether a key is repository content worth bringing back. Locks are deliberately excluded: the
    /// engine removes its own when it finishes, and restoring one would block the repository.
    /// </summary>
    private static bool IsRecoverableContent(string key, string prefix)
    {
        var relative = prefix.Length == 0
            ? key
            : key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..].TrimStart('/') : key;

        return !relative.StartsWith("locks/", StringComparison.Ordinal);
    }

    private static async Task<List<string>> MarkersForAsync(
        IAmazonS3 client,
        ObjectStorageAddress address,
        string key,
        CancellationToken cancellationToken)
    {
        var markers = new List<string>();
        await foreach (var version in AllVersionsAsync(client, address, cancellationToken))
        {
            if (version.Key == key && version.IsDeleteMarker == true)
            {
                markers.Add(version.VersionId);
            }
        }

        return markers;
    }

    private static async IAsyncEnumerable<S3ObjectVersion> AllVersionsAsync(
        IAmazonS3 client,
        ObjectStorageAddress address,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListVersionsRequest { BucketName = address.Bucket, Prefix = address.Prefix };

        while (true)
        {
            var page = await client.ListVersionsAsync(request, cancellationToken);
            foreach (var version in page.Versions)
            {
                yield return version;
            }

            if (page.IsTruncated != true)
            {
                yield break;
            }

            request.KeyMarker = page.NextKeyMarker;
            request.VersionIdMarker = page.NextVersionIdMarker;
        }
    }

    private async Task<(IAmazonS3 Client, ObjectStorageAddress Address)> ConnectAsync(
        string repositoryLocation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);
        if (!RepositoryLocation.IsObjectStorage(repositoryLocation))
        {
            throw new ArgumentException(
                "Only a repository in object storage can have objects hidden behind delete markers.",
                nameof(repositoryLocation));
        }

        var address = RepositoryLocation.ParseObjectStorage(repositoryLocation);
        var credentials = await _credentials.ForRepositoryAsync(repositoryLocation, cancellationToken)
            ?? throw new InvalidOperationException("No credentials are available for that storage.");

        var client = new AmazonS3Client(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = address.Endpoint.ToString(),
                ForcePathStyle = true,
                AuthenticationRegion = credentials.Region ?? "us-east-1"
            });

        return (client, address);
    }
}
