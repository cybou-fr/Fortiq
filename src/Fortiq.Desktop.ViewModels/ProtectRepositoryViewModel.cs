using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Fortiq.Desktop.ViewModels;

/// <summary>What creating a protected repository needs, and what it produced.</summary>
/// <param name="StorageAccessKeyId">Object storage key, when the repository is in object storage.</param>
/// <param name="StorageSecretKey">Its secret. Held only long enough to be written to the machine store.</param>
/// <param name="StorageRegion">The region the endpoint signs requests for.</param>
public sealed record ProtectRepositoryRequest(
    string RepositoryLocation,
    string KitDirectory,
    string SourcePath,
    string? StorageAccessKeyId = null,
    string? StorageSecretKey = null,
    string? StorageRegion = null)
{
    public override string ToString() => "ProtectRepositoryRequest { storage credentials = [redacted] }";
}

public sealed record ProtectedRepositoryResult(
    string RepositoryId,
    string RecoveryMnemonic,
    bool DeviceUnlockAvailable,
    bool BackupScheduled = true,
    string? SchedulingFailure = null)
{
    // The result crosses the desktop boundary while it still contains the only displayable copy of
    // the disaster secret. Keep an accidental log or debugger rendering from disclosing it.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("RepositoryId = ").Append(RepositoryId)
            .Append(", RecoveryMnemonic = [redacted]")
            .Append(", DeviceUnlockAvailable = ").Append(DeviceUnlockAvailable)
            .Append(", BackupScheduled = ").Append(BackupScheduled)
            .Append(", SchedulingFailure = ").Append(SchedulingFailure);
        return true;
    }
}

/// <summary>Creates the repository. The desktop knows nothing about engines, kits or keys.</summary>
public interface IProtectRepository
{
    Task<ProtectedRepositoryResult> CreateAsync(ProtectRepositoryRequest request, CancellationToken cancellationToken);
}

/// <summary>Where the wizard is.</summary>
public enum ProtectStep
{
    /// <summary>Choosing what to protect and where.</summary>
    Describe,

    /// <summary>The repository exists and the recovery mnemonic is on screen, once.</summary>
    WriteDownRecoveryMaterial,

    /// <summary>Typing back part of the mnemonic, to show it was really written down.</summary>
    ConfirmRecoveryMaterial,

    /// <summary>Confirmed. The mnemonic is no longer available anywhere.</summary>
    Done
}

/// <summary>
/// The wizard that creates a protected repository. Its whole shape follows one rule from the product:
/// it does not finish until the person has shown they can reproduce the recovery material.
/// </summary>
/// <remarks>
/// The mnemonic exists in this object only between creating the repository and confirming it, and is
/// cleared the moment the wizard finishes. Fortiq cannot produce it again, so a wizard that let
/// someone click past it would be handing out a repository nobody can open.
/// </remarks>
public sealed class ProtectRepositoryViewModel : INotifyPropertyChanged
{
    private const int WordsToConfirm = 3;

    private readonly IProtectRepository _protect;
    private readonly Func<int, int, int> _pickWord;

    private string _repositoryLocation = string.Empty;
    private string _kitDirectory = string.Empty;
    private string _sourcePath = string.Empty;
    private string _storageAccessKeyId = string.Empty;
    private string _storageSecretKey = string.Empty;
    private string _storageRegion = string.Empty;
    private string _confirmationInput = string.Empty;
    private string? _mnemonic;
    private string? _failure;
    private bool _busy;
    private ProtectStep _step = ProtectStep.Describe;

    public ProtectRepositoryViewModel(IProtectRepository protect, Func<int, int, int>? pickWord = null, bool automaticBackupsAvailable = true)
    {
        AutomaticBackupsAvailable = automaticBackupsAvailable;
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _pickWord = pickWord ?? ((count, _) => Random.Shared.Next(count));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProtectStep Step { get => _step; private set => Set(ref _step, value); }

    public string RepositoryLocation { get => _repositoryLocation; set => Set(ref _repositoryLocation, value); }

    public string KitDirectory { get => _kitDirectory; set => Set(ref _kitDirectory, value); }

    public string SourcePath { get => _sourcePath; set => Set(ref _sourcePath, value); }

    /// <summary>
    /// The object storage credentials, asked for here because there was nowhere else to type them.
    /// </summary>
    /// <remarks>
    /// A person choosing "S3-compatible cloud storage" in a desktop wizard had no way to say which
    /// account: the only paths in were an environment variable and a service command line. The
    /// wizard offered the choice and then could not carry it out, which is the worst combination.
    ///
    /// These are held in memory for as long as the wizard is open and written to the machine
    /// credential store on completion, per repository. They are not stored on the view model beyond
    /// that, and the window's field for the secret is masked.
    /// </remarks>
    public string StorageAccessKeyId { get => _storageAccessKeyId; set => Set(ref _storageAccessKeyId, value); }

    public string StorageSecretKey { get => _storageSecretKey; set => Set(ref _storageSecretKey, value); }

    public string StorageRegion { get => _storageRegion; set => Set(ref _storageRegion, value); }

    /// <summary>True when the destination is object storage, which is what makes the keys required.</summary>
    public bool NeedsStorageCredentials =>
        RepositoryLocation.StartsWith("s3:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The recovery mnemonic, shown once and only while the wizard is on that step.</summary>
    public string? RecoveryMnemonic => Step is ProtectStep.WriteDownRecoveryMaterial ? _mnemonic : null;

    /// <summary>Which words the person is asked to type back, in the order they are asked for.</summary>
    public IReadOnlyList<int> RequestedWordNumbers { get; private set; } = [];

    public string ConfirmationInput { get => _confirmationInput; set => Set(ref _confirmationInput, value); }

    public string? Failure { get => _failure; private set => Set(ref _failure, value); }

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public string? RepositoryId { get; private set; }

    public bool DeviceUnlockAvailable { get; private set; }

    /// <summary>Whether the service schedule was committed after the repository and kit.</summary>
    public bool BackupScheduled { get; private set; }

    /// <summary>
    /// An actionable warning when recovery material is safe but unattended backup setup failed.
    /// This is separate from <see cref="Failure"/> because it must survive mnemonic confirmation.
    /// </summary>
    public string? SchedulingFailure { get; private set; }

    public bool AutomaticBackupsAvailable { get; }

    public bool CanClose => !Busy && Step is ProtectStep.Describe or ProtectStep.Done;

    public void ClearStorageCredentials()
    {
        StorageSecretKey = string.Empty;
        StorageAccessKeyId = string.Empty;
    }

    public bool CanCreate =>
        !Busy
        && Step == ProtectStep.Describe
        && !string.IsNullOrWhiteSpace(RepositoryLocation)
        && !string.IsNullOrWhiteSpace(KitDirectory)
        && !string.IsNullOrWhiteSpace(SourcePath)
        // Object storage without keys cannot be reached, so creating would fail after the recovery
        // phrase had been shown - the one point in this wizard where a failure costs the most.
        && (!NeedsStorageCredentials
            || (!string.IsNullOrWhiteSpace(StorageAccessKeyId) && !string.IsNullOrWhiteSpace(StorageSecretKey)));

    public async Task CreateAsync(CancellationToken cancellationToken)
    {
        if (!CanCreate)
        {
            return;
        }

        Busy = true;
        Failure = null;
        try
        {
            var result = await _protect.CreateAsync(
                new ProtectRepositoryRequest(
                    RepositoryLocation,
                    KitDirectory,
                    SourcePath,
                    NeedsStorageCredentials ? StorageAccessKeyId : null,
                    NeedsStorageCredentials ? StorageSecretKey : null,
                    NeedsStorageCredentials ? StorageRegion : null),
                cancellationToken);

            ClearStorageCredentials();
            RepositoryId = result.RepositoryId;
            DeviceUnlockAvailable = result.DeviceUnlockAvailable;
            BackupScheduled = result.BackupScheduled;
            SchedulingFailure = result.SchedulingFailure;
            _mnemonic = result.RecoveryMnemonic;
            Step = ProtectStep.WriteDownRecoveryMaterial;
            OnPropertyChanged(nameof(RecoveryMnemonic));
            OnPropertyChanged(nameof(BackupScheduled));
            OnPropertyChanged(nameof(SchedulingFailure));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Failure = error.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Moves on to typing the words back. The mnemonic stops being readable at this point.</summary>
    public void WroteItDown()
    {
        if (Step != ProtectStep.WriteDownRecoveryMaterial)
        {
            return;
        }

        var words = _mnemonic!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var requested = new List<int>();
        while (requested.Count < Math.Min(WordsToConfirm, words.Length))
        {
            var index = _pickWord(words.Length, requested.Count);
            if (!requested.Contains(index + 1))
            {
                requested.Add(index + 1);
            }
        }

        requested.Sort();
        RequestedWordNumbers = requested;
        Step = ProtectStep.ConfirmRecoveryMaterial;
        OnPropertyChanged(nameof(RequestedWordNumbers));
        OnPropertyChanged(nameof(RecoveryMnemonic));
    }

    /// <summary>Goes back to the mnemonic. Someone who did not manage to write it down needs it again.</summary>
    public void ShowItAgain()
    {
        if (Step != ProtectStep.ConfirmRecoveryMaterial)
        {
            return;
        }

        ConfirmationInput = string.Empty;
        Failure = null;
        Step = ProtectStep.WriteDownRecoveryMaterial;
        OnPropertyChanged(nameof(RecoveryMnemonic));
    }

    /// <summary>
    /// Checks the words typed back. Only a correct answer finishes the wizard, and finishing clears
    /// the mnemonic from memory: from then on the written copy is the only one.
    /// </summary>
    public bool Confirm()
    {
        if (Step != ProtectStep.ConfirmRecoveryMaterial)
        {
            return false;
        }

        var words = _mnemonic!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expected = RequestedWordNumbers.Select(number => words[number - 1]);
        var typed = ConfirmationInput.Split(
            [' ', ',', '\t', '\n', '\r'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!expected.SequenceEqual(typed.Select(word => word.ToLowerInvariant())))
        {
            Failure = "Those are not the words asked for. Check the copy you wrote down.";
            return false;
        }

        _mnemonic = null;
        ConfirmationInput = string.Empty;
        Failure = null;
        Step = ProtectStep.Done;
        OnPropertyChanged(nameof(RecoveryMnemonic));
        return true;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(property);
        if (property is nameof(RepositoryLocation) or nameof(KitDirectory) or nameof(SourcePath) or nameof(Busy) or nameof(Step) or nameof(StorageAccessKeyId) or nameof(StorageSecretKey))
        {
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
