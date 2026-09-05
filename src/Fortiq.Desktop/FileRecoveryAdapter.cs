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
        var result = await ExecuteAsync(access, RecoveryOperation.Snapshots, null, null, null, token);
        RequireSchema(result, "fortiq.recovery-snapshots");
        return result.GetProperty("snapshots").EnumerateArray().Select(item => new RecoverySnapshot(
            item.GetProperty("id").GetString() ?? throw new InvalidDataException("A backup has no identifier."),
            item.GetProperty("createdAt").GetDateTimeOffset(),
            item.GetProperty("path").GetString() ?? throw new InvalidDataException("A backup has no source path."))).ToArray();
    }

    public async Task<IReadOnlyList<SnapshotFileItem>> ListFilesAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, CancellationToken token)
    {
        var result = await ExecuteAsync(access, RecoveryOperation.Files, snapshot, null, null, token);
        RequireSchema(result, "fortiq.recovery-files");
        return result.GetProperty("files").EnumerateArray().Select(item => new SnapshotFileItem(
            item.GetProperty("name").GetString() ?? "",
            item.GetProperty("path").GetString() ?? "",
            item.GetProperty("type").GetString() ?? "file",
            item.TryGetProperty("size", out var size) ? size.GetUInt64() : 0,
            item.TryGetProperty("mtime", out var mtime) && mtime.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(mtime.GetString(), out var parsedMtime) ? parsedMtime : null
        )).ToArray();
    }

    public Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, CancellationToken token = default) =>
        RestoreAsync(access, snapshot, target, null, token);

    public async Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, string? specificPath, CancellationToken token = default)
    {
        ValidateDestination(access, snapshot, target);
        var result = await ExecuteAsync(access, RecoveryOperation.Restore, snapshot, Path.GetFullPath(target), specificPath, token);
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
        RecoverySnapshot? snapshot, string? target, string? specificSource, CancellationToken token) => Task.Run(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(access.Mnemonic);
        ArgumentException.ThrowIfNullOrWhiteSpace(access.Kit);
        ArgumentException.ThrowIfNullOrWhiteSpace(access.Repository);
        var storage = new SessionStorage(access);
        var executor = executorFactory?.Invoke(storage) ?? new RecoveryCommandExecutor(runDirectory: runDirectory, storage: storage);
        var sourcePath = specificSource ?? snapshot?.SourcePath;
        var command = new RecoveryCommand(operation, RepositoryLocation.Normalize(access.Repository), engineRoot,
            Path.GetFullPath(access.Kit), snapshot?.Id, target, sourcePath);
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
