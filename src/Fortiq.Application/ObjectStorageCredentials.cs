namespace Fortiq.Application;

/// <summary>
/// The identity Fortiq uses to reach object storage. It is not the repository's encryption secret:
/// the repository is encrypted before anything is uploaded, so an attacker holding these credentials
/// can see how much data there is and, unless the bucket forbids it, delete it - but cannot read it.
/// </summary>
/// <remarks>
/// Kept separate from the engine unlock secret on purpose. The two protect different things and are
/// meant to be held by different identities: a backup that can write must not need an identity that
/// can also delete history.
/// </remarks>
public sealed record ObjectStorageCredentials(string AccessKeyId, string SecretAccessKey, string? Region = null)
{
    /// <summary>Keeps the secret out of the generated <c>ToString</c>, which reaches logs and dumps.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("AccessKeyId = ").Append(AccessKeyId)
            .Append(", SecretAccessKey = [redacted]")
            .Append(", Region = ").Append(Region);
        return true;
    }
}

/// <summary>
/// Supplies storage credentials for a repository, or nothing when it needs none - a repository in a
/// local directory does.
/// </summary>
public interface IObjectStorageCredentialProvider
{
    Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken);
}

/// <summary>The provider used when no object storage is involved.</summary>
public sealed class NoObjectStorageCredentials : IObjectStorageCredentialProvider
{
    public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
        Task.FromResult<ObjectStorageCredentials?>(null);
}

/// <summary>
/// Reads the standard AWS process environment explicitly. The engine process does not inherit the
/// ambient environment; these values are copied into its allow-listed, per-invocation environment
/// only when the target repository is object storage.
/// </summary>
public sealed class EnvironmentObjectStorageCredentialProvider : IObjectStorageCredentialProvider
{
    public Task<ObjectStorageCredentials?> ForRepositoryAsync(
        string repositoryLocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Fortiq.Domain.RepositoryLocation.IsObjectStorage(repositoryLocation))
        {
            return Task.FromResult<ObjectStorageCredentials?>(null);
        }

        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return Task.FromResult<ObjectStorageCredentials?>(null);
        }

        return Task.FromResult<ObjectStorageCredentials?>(new ObjectStorageCredentials(
            accessKey,
            secretKey,
            Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")));
    }
}
