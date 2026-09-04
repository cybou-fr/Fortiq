using Amazon.S3;
using Amazon.S3.Model;
using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Infrastructure.ObjectStorage;

/// <summary>
/// Asks an S3-compatible bucket what it will refuse. Object locking can only be turned on when a
/// bucket is created, so this is a fact about the bucket rather than a setting Fortiq could apply
/// on its behalf - which is exactly why it has to be checked rather than assumed.
/// </summary>
public sealed class S3StorageProtectionInspector : IStorageProtectionInspector
{
    private readonly IObjectStorageCredentialProvider _credentials;

    public S3StorageProtectionInspector(IObjectStorageCredentialProvider credentials) =>
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public async Task<StorageProtection> InspectAsync(string repositoryLocation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);

        if (!RepositoryLocation.IsObjectStorage(repositoryLocation))
        {
            // A directory on this machine keeps nothing safe from whoever can write to it, and
            // saying so is more useful than refusing to answer.
            return StorageProtection.None;
        }

        var address = RepositoryLocation.ParseObjectStorage(repositoryLocation);
        var credentials = await _credentials.ForRepositoryAsync(repositoryLocation, cancellationToken)
            ?? throw new StorageNotImmutableException(
                "Storage protection cannot be established without credentials for that storage.");

        using var client = new AmazonS3Client(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = address.Endpoint.ToString(),
                ForcePathStyle = true,
                AuthenticationRegion = credentials.Region ?? "us-east-1"
            });

        try
        {
            var configuration = await client.GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = address.Bucket },
                cancellationToken);

            var lockConfiguration = configuration.ObjectLockConfiguration;
            if (lockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled)
            {
                return StorageProtection.None;
            }

            var retention = lockConfiguration.Rule?.DefaultRetention;
            var mode = retention?.Mode == ObjectLockRetentionMode.Compliance ? RetentionMode.Compliance
                : retention?.Mode == ObjectLockRetentionMode.Governance ? RetentionMode.Governance
                : RetentionMode.None;
            var duration = Duration(retention);
            var active = mode is RetentionMode.Governance or RetentionMode.Compliance
                && duration is { } value
                && value > TimeSpan.Zero;

            return new StorageProtection(
                Immutable: active,
                Mode: mode,
                DefaultRetention: duration,
                ObjectLockCapable: true);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode is "ObjectLockConfigurationNotFoundError")
        {
            // The bucket exists and has no locking: an answer, not a failure.
            return StorageProtection.None;
        }
    }

    private static TimeSpan? Duration(DefaultRetention? retention) => retention switch
    {
        null => null,
        { Days: > 0 and { } days } => TimeSpan.FromDays(days),
        { Years: > 0 and { } years } => TimeSpan.FromDays(years * 365),
        _ => null
    };
}
