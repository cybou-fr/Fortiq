using System.Text.Json;
using Fortiq.Application;
using Fortiq.Desktop.ViewModels;
using Fortiq.Domain;
using Fortiq.Recover;

namespace Fortiq.Desktop;

/// <summary>Uses the emergency executor directly; secrets never become process arguments or saved settings.</summary>
public sealed class FileRecoveryAdapter(string engineRoot, string runDirectory,
    Func<IObjectStorageCredentialProvider, IRecoveryCommandExecutor>? executorFactory = null) : IFileRecovery
{
    public async Task<IReadOnlyList<RecoverySnapshot>> ListAsync(FileRecoveryAccess access, CancellationToken token)
    {
        var result = await ExecuteAsync(access, RecoveryOperation.Snapshots, null, null, token);
        RequireSchema(result, "fortiq.recovery-snapshots");
        return result.GetProperty("snapshots").EnumerateArray().Select(item => new RecoverySnapshot(
            item.GetProperty("id").GetString() ?? throw new InvalidDataException("A backup has no identifier."),
            item.GetProperty("createdAt").GetDateTimeOffset(),
            item.GetProperty("path").GetString() ?? throw new InvalidDataException("A backup has no source path."))).ToArray();
    }

    public async Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, CancellationToken token)
    {
        ValidateDestination(access, snapshot, target);
        var result = await ExecuteAsync(access, RecoveryOperation.Restore, snapshot, Path.GetFullPath(target), token);
        RequireSchema(result, "fortiq.recovery-restore");
        return new FileRecoveryResult(result.GetProperty("target").GetString()
            ?? throw new InvalidDataException("Recovery returned no destination."), result.GetProperty("bytesRestored").GetUInt64());
    }

    public static void ValidateDestination(FileRecoveryAccess access, RecoverySnapshot snapshot, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!Path.IsPathFullyQualified(target)) throw new IOException("Choose an absolute destination path.");
        var full = Path.GetFullPath(target);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Choose a new destination folder. Existing folders are never overwritten.");
        var parent = Directory.GetParent(full) ?? throw new IOException("A drive root cannot be a recovery destination.");
        if (!parent.Exists) throw new IOException("The destination parent folder does not exist. Choose an existing folder.");
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Choose a destination without symbolic links or junctions in its path.");

        foreach (var protectedPath in new[] { access.Kit, snapshot.SourcePath,
            RepositoryLocation.IsObjectStorage(access.Repository) ? string.Empty : access.Repository })
        {
            if (string.IsNullOrWhiteSpace(protectedPath)) continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.Equals(root, comparison) || full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Choose a destination outside the original source, backup repository and recovery kit.");
        }
    }

    private Task<JsonElement> ExecuteAsync(FileRecoveryAccess access, RecoveryOperation operation,
        RecoverySnapshot? snapshot, string? target, CancellationToken token) => Task.Run(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(access.Mnemonic);
        ArgumentException.ThrowIfNullOrWhiteSpace(access.Kit);
        ArgumentException.ThrowIfNullOrWhiteSpace(access.Repository);
        var storage = new SessionStorage(access);
        var executor = executorFactory?.Invoke(storage) ?? new RecoveryCommandExecutor(runDirectory: runDirectory, storage: storage);
        var command = new RecoveryCommand(operation, RepositoryLocation.Normalize(access.Repository), engineRoot,
            Path.GetFullPath(access.Kit), snapshot?.Id, target, snapshot?.SourcePath);
        var result = await executor.ExecuteAsync(command, new SessionMaterial(access.Mnemonic), token);
        return JsonSerializer.SerializeToElement(result);
    }, token);

    private static void RequireSchema(JsonElement result, string schema)
    {
        if (result.GetProperty("schema").GetString() != schema || result.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("The recovery result format is not supported.");
    }

    private sealed class SessionMaterial(string mnemonic) : IRecoveryMaterialReader
    {
        public Task<string> ReadMnemonicAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(mnemonic);
        }
    }

    private sealed class SessionStorage(FileRecoveryAccess access) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RepositoryLocation.IsObjectStorage(repositoryLocation)) return Task.FromResult<ObjectStorageCredentials?>(null);
            if (string.IsNullOrWhiteSpace(access.AccessKey) || string.IsNullOrWhiteSpace(access.SecretKey))
                throw new InvalidOperationException("Enter the S3 access key and secret key for this repository.");
            return Task.FromResult<ObjectStorageCredentials?>(new ObjectStorageCredentials(access.AccessKey, access.SecretKey,
                string.IsNullOrWhiteSpace(access.Region) ? null : access.Region));
        }
    }
}
