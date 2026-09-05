using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop.ViewModels;

public interface IInstallationOperations
{
    Task<int> ExecuteInstallAsync(string targetDir, bool installService, bool addToPath, bool autoStartOnLogon, IProgress<(string Message, double Percent)> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Observable view model driving the Fortiq First-Run Installation & Prerequisite Wizard.
/// </summary>
public sealed class InstallViewModel : INotifyPropertyChanged
{
    private readonly IInstallationInspector _inspector;
    private readonly IInstallationOperations? _operations;

    private bool _isInspecting = true;
    private bool _isInstalling;
    private SystemInstallationStatus? _status;
    private string _installDirectory;
    private bool _installService = true;
    private bool _addToPath = true;
    private bool _autoStartOnLogon = true;
    private string _progressMessage = "Ready to install.";
    private double _progressPercent;
    private string? _errorMessage;

    public InstallViewModel(
        IInstallationInspector inspector,
        SystemInstallationStatus? initialStatus = null,
        IInstallationOperations? operations = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _operations = operations;
        _status = initialStatus;

        _installDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fortiq")
            : Path.Combine(AppContext.BaseDirectory, "installed");

        if (_status is not null)
        {
            _isInspecting = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RequestCloseAndLaunchMain;
    public event Action? RequestCloseAndLaunchPortable;

    public bool IsInspecting
    {
        get => _isInspecting;
        private set => SetField(ref _isInspecting, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetField(ref _isInstalling, value))
            {
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(CanRunPortable));
            }
        }
    }

    public SystemInstallationStatus? Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(PrerequisitesMet));
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(TpmAvailable));
                OnPropertyChanged(nameof(TpmDetail));
                OnPropertyChanged(nameof(RuntimeValid));
                OnPropertyChanged(nameof(RuntimeDetail));
                OnPropertyChanged(nameof(EngineVerified));
                OnPropertyChanged(nameof(EngineDetail));
                OnPropertyChanged(nameof(VssAvailable));
                OnPropertyChanged(nameof(VssDetail));
            }
        }
    }

    public string InstallDirectory
    {
        get => _installDirectory;
        set
        {
            if (SetField(ref _installDirectory, value))
            {
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool InstallService
    {
        get => _installService;
        set => SetField(ref _installService, value);
    }

    public bool AddToPath
    {
        get => _addToPath;
        set => SetField(ref _addToPath, value);
    }

    public bool AutoStartOnLogon
    {
        get => _autoStartOnLogon;
        set => SetField(ref _autoStartOnLogon, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        set => SetField(ref _progressMessage, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public bool PrerequisitesMet => _status is null || (_status.Platform.DotNetRuntimeValid && _status.Engine.HashVerified);

    public bool CanInstall => !IsInstalling && !string.IsNullOrWhiteSpace(InstallDirectory) && PrerequisitesMet;

    public bool CanRunPortable => !IsInstalling;

    public bool TpmAvailable => _status?.Platform.TpmAvailable ?? false;

    /// <remarks>
    /// Plain sentence first, detail after. The reader of this screen is deciding whether to install a
    /// backup tool, not auditing a key hierarchy - "TPM 2.0 silicon provider available
    /// (non-exportable device key protection)" told them nothing they could act on, and told the one
    /// reader who knew what it meant something they could have assumed.
    /// </remarks>
    public string TpmDetail => TpmAvailable
        ? "This PC has a security chip, so Fortiq can unlock your backups here without a password. Your written recovery words still work anywhere."
        : "No security chip found. Fortiq will use your written recovery words instead - keep them somewhere safe, because they are the only way back in.";

    public bool RuntimeValid => _status?.Platform.DotNetRuntimeValid ?? true;

    public string RuntimeDetail => RuntimeValid
        ? $"Everything Fortiq needs to run is present (.NET {_status?.Platform.DotNetVersion ?? Environment.Version.ToString()})."
        : $"A required Windows component is missing or too old (.NET {_status?.Platform.DotNetVersion ?? Environment.Version.ToString()}). Install the .NET 10 Desktop Runtime, then reopen Fortiq.";

    public bool EngineVerified => _status?.Engine.HashVerified ?? false;

    public string EngineDetail => EngineVerified
        ? $"The program that writes your backups is present and unmodified (restic {_status?.Engine.RequiredVersion}, checked byte for byte)."
        : $"The program that writes your backups is missing or does not match what Fortiq expects (restic {_status?.Engine.RequiredVersion ?? "not found"}). Fortiq will not run an unverified copy.";

    public bool VssAvailable => _status?.Platform.HasBackupPrivileges ?? false;

    /// <remarks>
    /// This row worried people for no reason. Its old text - "Interactive token does not hold
    /// SeBackupPrivilege" - reads like a fault, and is not one: the background service holds that
    /// privilege and does the work. Nothing is wrong, so the sentence says nothing is wrong.
    /// </remarks>
    public string VssDetail => VssAvailable
        ? "Files that are open and in use can be backed up safely."
        : "Files that are open and in use will still be backed up safely - the Fortiq background service handles those. Nothing to do here.";

    public async Task RefreshInspectionAsync(CancellationToken cancellationToken = default)
    {
        IsInspecting = true;
        ErrorMessage = null;
        try
        {
            Status = await _inspector.InspectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"System inspection failed: {ex.Message}";
        }
        finally
        {
            IsInspecting = false;
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!CanInstall) return;

        IsInstalling = true;
        ErrorMessage = null;
        ProgressMessage = "Starting installation...";
        ProgressPercent = 5;

        try
        {
            if (_operations is not null)
            {
                var progress = new Progress<(string Message, double Percent)>(p =>
                {
                    ProgressMessage = p.Message;
                    ProgressPercent = p.Percent;
                });

                var exitCode = await _operations.ExecuteInstallAsync(InstallDirectory, InstallService, AddToPath, AutoStartOnLogon, progress, cancellationToken);
                if (exitCode == 66)
                {
                    ErrorMessage = "Administrative elevation (UAC) was declined. Installation was cancelled.";
                    IsInstalling = false;
                    return;
                }
                if (exitCode != 0)
                {
                    ErrorMessage = $"Installation exited with error code {exitCode}.";
                    IsInstalling = false;
                    return;
                }
            }

            ProgressMessage = "Installation completed successfully!";
            ProgressPercent = 100;

            // Trigger transition to main application window
            RequestCloseAndLaunchMain?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Installation failed: {ex.Message}";
            ProgressMessage = "Installation stopped due to an error.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    public void RunPortable()
    {
        if (!CanRunPortable) return;
        RequestCloseAndLaunchPortable?.Invoke();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
