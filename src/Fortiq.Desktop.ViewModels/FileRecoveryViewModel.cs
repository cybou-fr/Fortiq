using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Fortiq.Desktop.ViewModels;

public sealed record RecoverySnapshot(string Id, DateTimeOffset CreatedAt, string SourcePath)
{
    public override string ToString() => $"{CreatedAt.ToLocalTime():g} | {SourcePath} | {Id[..Math.Min(12, Id.Length)]}";
}

public sealed record SnapshotFileItem(
    string Name,
    string Path,
    string Type,
    ulong Size,
    DateTimeOffset? ModifiedAt)
{
    public bool IsDirectory => string.Equals(Type, "dir", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => IsDirectory ? $"{Name}/" : Name;

    public string FormattedSize => IsDirectory ? "<DIR>" : FormatBytes(Size);

    public string FormattedTime => ModifiedAt.HasValue
        ? ModifiedAt.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        : string.Empty;

    private static string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return FormattableString.Invariant($"{(double)bytes / 1024:F1} KB");
        if (bytes < 1024 * 1024 * 1024) return FormattableString.Invariant($"{(double)bytes / (1024 * 1024):F1} MB");
        return FormattableString.Invariant($"{(double)bytes / (1024 * 1024 * 1024):F1} GB");
    }
}

public sealed record FileRecoveryAccess(string Repository, string Kit, string Mnemonic,
    string AccessKey = "", string SecretKey = "", string Region = "")
{
    public override string ToString() => "FileRecoveryAccess { secrets = [redacted] }";
}

public sealed record FileRecoveryResult(string Target, ulong BytesRestored);

public interface IFileRecovery
{
    Task<IReadOnlyList<RecoverySnapshot>> ListAsync(FileRecoveryAccess access, CancellationToken token);
    Task<IReadOnlyList<SnapshotFileItem>> ListFilesAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, CancellationToken token);
    Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, CancellationToken token = default) =>
        RestoreAsync(access, snapshot, target, null, token);
    Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, string? specificPath, CancellationToken token = default);
}

/// <summary>A recovery session keeps its access material only until success or closing.</summary>
public sealed class FileRecoveryViewModel(IFileRecovery recovery) : INotifyPropertyChanged
{
    private CancellationTokenSource? _operation;
    private FileRecoveryAccess? _access;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<RecoverySnapshot> Snapshots { get; private set; } = [];
    public IReadOnlyList<SnapshotFileItem> Files { get; private set; } = [];
    public IReadOnlyList<SnapshotFileItem> FilteredFiles { get; private set; } = [];
    public SnapshotFileItem? SelectedFile { get; set; }
    public string SearchQuery { get; private set; } = string.Empty;
    public bool RestoreSpecificItem { get; set; }
    public bool FilesLoading { get; private set; }
    public bool Busy { get; private set; }
    public bool Completed { get; private set; }
    public string Status { get; private set; } = "Enter the recovery material to find your backups.";
    public string? RestoredTarget { get; private set; }

    public async Task LoadAsync(FileRecoveryAccess access)
    {
        if (Busy) return;
        _access = null;
        Snapshots = [];
        Files = [];
        FilteredFiles = [];
        SelectedFile = null;
        SearchQuery = string.Empty;
        RestoreSpecificItem = false;
        Completed = false;
        RestoredTarget = null;
        await RunAsync("Opening the repository and reading backups...", async token =>
        {
            var snapshots = await recovery.ListAsync(access, token);
            token.ThrowIfCancellationRequested();
            Snapshots = snapshots.OrderByDescending(item => item.CreatedAt).ToArray();
            if (Snapshots.Count > 0) _access = access;
            Status = Snapshots.Count == 0 ? "This repository contains no backups." : "Choose a backup and a destination to restore.";
        });
    }

    public async Task LoadFilesAsync(RecoverySnapshot snapshot)
    {
        if (Busy || _access is null) return;
        Files = [];
        FilteredFiles = [];
        SelectedFile = null;
        FilesLoading = true;
        Changed(nameof(FilesLoading));
        Changed(nameof(Files));
        Changed(nameof(FilteredFiles));

        try
        {
            var access = _access;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var files = await recovery.ListFilesAsync(access, snapshot, cts.Token);
            Files = files;
            ApplyFilter();
        }
        catch (Exception error)
        {
            Status = "Could not load file list from backup: " + error.Message;
            Changed(nameof(Status));
        }
        finally
        {
            FilesLoading = false;
            Changed(nameof(FilesLoading));
        }
    }

    public const int MaxDisplayResults = 500;
    public string SearchFilterSummary { get; private set; } = string.Empty;

    public void SetSearchQuery(string query)
    {
        SearchQuery = query ?? string.Empty;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            FilteredFiles = Files.Count <= MaxDisplayResults
                ? Files
                : Files.Take(MaxDisplayResults).ToArray();

            SearchFilterSummary = Files.Count > MaxDisplayResults
                ? $"Showing first {MaxDisplayResults:N0} of {Files.Count:N0} files. Use search to filter."
                : $"{Files.Count:N0} files in backup. Select one to restore.";
        }
        else
        {
            var query = SearchQuery;
            var matches = Files
                .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            f.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            FilteredFiles = matches.Count <= MaxDisplayResults
                ? matches
                : matches.Take(MaxDisplayResults).ToArray();

            SearchFilterSummary = matches.Count > MaxDisplayResults
                ? $"Showing top {MaxDisplayResults:N0} of {matches.Count:N0} files matching \"{query}\". Refine query for more specific results."
                : matches.Count == 0
                    ? $"No files match \"{query}\"."
                    : $"Showing {matches.Count:N0} files matching \"{query}\".";
        }
        Changed(nameof(FilteredFiles));
        Changed(nameof(SearchFilterSummary));
    }

    public async Task RestoreAsync(RecoverySnapshot snapshot, string target, string? specificPath = null)
    {
        if (Busy || Completed || _access is null) return;
        if (!Snapshots.Contains(snapshot)) throw new ArgumentException("Select a backup from this recovery session.", nameof(snapshot));
        var access = _access;
        var pathToRestore = specificPath ?? (RestoreSpecificItem && SelectedFile is not null ? SelectedFile.Path : null);
        await RunAsync("Restoring files. Keep this window open...", async token =>
        {
            var result = await recovery.RestoreAsync(access, snapshot, target, pathToRestore, token);
            RestoredTarget = result.Target;
            Completed = true;
            _access = null;
            Status = $"Restored {result.BytesRestored:N0} bytes. Your files are in {result.Target}";
        });
    }

    public void Cancel()
    {
        if (!Busy) return;
        Status = "Cancelling. Waiting for the engine to stop safely...";
        Changed();
        _operation?.Cancel();
    }

    public void Clear()
    {
        if (Busy) throw new InvalidOperationException("Wait for recovery to stop before closing the session.");
        _access = null;
        Snapshots = [];
        Files = [];
        FilteredFiles = [];
        SelectedFile = null;
        SearchQuery = string.Empty;
        RestoreSpecificItem = false;
        FilesLoading = false;
        Completed = false;
        RestoredTarget = null;
        Status = "Enter the recovery material to find your backups.";
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Busy = true;
        Status = status;
        Changed();
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status = "Recovery was cancelled. No completed recovery is being claimed. Check the destination before trying again.";
        }
        catch (Exception error)
        {
            Status = "Recovery did not complete. " + error.Message;
        }
        finally
        {
            _operation = null;
            Busy = false;
            Changed();
        }
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
