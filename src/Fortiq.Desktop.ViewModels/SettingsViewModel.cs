using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fortiq.Desktop.ViewModels;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>
/// Presentation model for application settings, service lifecycle, and diagnostics.
/// Adheres to Spec 23 Section 8 (Screen 6: Settings).
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private AppThemePreference _themePreference = AppThemePreference.System;
    private string _serviceStatus = "Unknown";
    private bool _isServiceRunning;
    private bool _isBusy;
    private string? _statusMessage;
    private bool _startWithWindows;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AppThemePreference>? ThemeChanged;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value))
            {
                if (OperatingSystem.IsWindows())
                {
                    Fortiq.Platform.Windows.WindowsAutostartController.SetAutostartEnabled(value);
                }
            }
        }
    }

    public AppThemePreference ThemePreference
    {
        get => _themePreference;
        set
        {
            if (Set(ref _themePreference, value))
            {
                ThemeChanged?.Invoke(value);
            }
        }
    }

    public string ServiceStatus
    {
        get => _serviceStatus;
        set => Set(ref _serviceStatus, value);
    }

    public bool IsServiceRunning
    {
        get => _isServiceRunning;
        set => Set(ref _isServiceRunning, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string AppVersion { get; }
    public string RuntimeVersion { get; }

    public Func<Task<bool>>? StartServiceAction { get; set; }
    public Func<Task<bool>>? StopServiceAction { get; set; }
    public Func<Task<string>>? RefreshServiceStatusAction { get; set; }

    public SettingsViewModel(string dataDirectory, string? logsDirectory = null)
    {
        DataDirectory = dataDirectory ?? string.Empty;
        LogsDirectory = logsDirectory ?? (string.IsNullOrEmpty(dataDirectory) ? string.Empty : Path.Combine(dataDirectory, "logs"));
        AppVersion = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        RuntimeVersion = Environment.Version.ToString(3);
        if (OperatingSystem.IsWindows())
        {
            _startWithWindows = Fortiq.Platform.Windows.WindowsAutostartController.IsAutostartEnabled();
        }
    }

    public async Task RefreshServiceStatusAsync()
    {
        if (RefreshServiceStatusAction == null) return;
        try
        {
            IsBusy = true;
            var status = await RefreshServiceStatusAction();
            ServiceStatus = status;
            IsServiceRunning = string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to check service: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ToggleServiceAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = null;
        try
        {
            if (IsServiceRunning)
            {
                if (StopServiceAction != null)
                {
                    if (!await StopServiceAction()) StatusMessage = "The service did not stop. Check administrator permissions and retry.";
                }
            }
            else
            {
                if (StartServiceAction != null)
                {
                    if (!await StartServiceAction()) StatusMessage = "The service did not start. Check administrator permissions and retry.";
                }
            }
            await RefreshServiceStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Service operation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenDataFolder()
    {
        if (Directory.Exists(DataDirectory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DataDirectory,
                UseShellExecute = true
            });
        }
    }

    public void OpenLogsFolder()
    {
        if (Directory.Exists(LogsDirectory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LogsDirectory,
                UseShellExecute = true
            });
        }
        else if (Directory.Exists(DataDirectory))
        {
            OpenDataFolder();
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return true;
    }
}
