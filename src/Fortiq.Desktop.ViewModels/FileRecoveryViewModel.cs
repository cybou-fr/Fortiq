using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Fortiq.Desktop.ViewModels;

public sealed record RecoverySnapshot(string Id, DateTimeOffset CreatedAt, string SourcePath)
{
    public override string ToString() => $"{CreatedAt.ToLocalTime():g} | {SourcePath} | {Id[..Math.Min(12, Id.Length)]}";
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
    Task<FileRecoveryResult> RestoreAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, string target, CancellationToken token);
}

/// <summary>A recovery session keeps its access material only until success or closing.</summary>
public sealed class FileRecoveryViewModel(IFileRecovery recovery) : INotifyPropertyChanged
{
    private CancellationTokenSource? _operation;
    private FileRecoveryAccess? _access;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<RecoverySnapshot> Snapshots { get; private set; } = [];
    public bool Busy { get; private set; }
    public bool Completed { get; private set; }
    public string Status { get; private set; } = "Enter the recovery material to find your backups.";
    public string? RestoredTarget { get; private set; }

    public async Task LoadAsync(FileRecoveryAccess access)
    {
        if (Busy) return;
        _access = null;
        Snapshots = [];
        Completed = false;
        RestoredTarget = null;
        await RunAsync("Opening the repository and reading backups...", async token =>
        {
            var snapshots = await recovery.ListAsync(access, token);
            token.ThrowIfCancellationRequested();
            Snapshots = snapshots.OrderByDescending(item => item.CreatedAt).ToArray();
            if (Snapshots.Count > 0) _access = access;
            Status = Snapshots.Count == 0 ? "This repository contains no backups." : "Choose a backup and a destination. All files in that backup's source folder will be restored.";
        });
    }

    public async Task RestoreAsync(RecoverySnapshot snapshot, string target)
    {
        if (Busy || Completed || _access is null) return;
        if (!Snapshots.Contains(snapshot)) throw new ArgumentException("Select a backup from this recovery session.", nameof(snapshot));
        var access = _access;
        await RunAsync("Restoring files. Keep this window open...", async token =>
        {
            var result = await recovery.RestoreAsync(access, snapshot, target, token);
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
