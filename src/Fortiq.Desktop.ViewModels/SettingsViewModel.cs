using System.ComponentModel;
using System.Linq;
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

    public Func<bool, bool>? SetAutostartAction { get; set; }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value))
            {
                if (SetAutostartAction != null)
                {
                    var success = SetAutostartAction(value);
                    if (!success)
                    {
                        _startWithWindows = !value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartWithWindows)));
                        StatusMessage = "Could not update Windows startup registration in the registry.";
                    }
                    else
                    {
                        StatusMessage = null;
                    }
                }
                else if (OperatingSystem.IsWindows())
                {
                    var success = Fortiq.Platform.Windows.WindowsAutostartController.SetAutostartEnabled(value);
                    if (!success)
                    {
                        _startWithWindows = !value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartWithWindows)));
                        StatusMessage = "Could not update Windows startup registration in the registry.";
                    }
                    else
                    {
                        StatusMessage = null;
                    }
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

    /// <param name="startWithWindows">
    /// The initial autostart state. Left null, it is read from the machine.
    /// </param>
    /// <remarks>
    /// The parameter exists because reading the machine unconditionally made this view model's tests
    /// depend on the developer's registry: installing Fortiq with autostart enabled turned a passing
    /// test red without a line of it changing. A test that reads the machine it runs on is not
    /// testing the code.
    /// </remarks>
    public SettingsViewModel(string dataDirectory, string? logsDirectory = null, bool? startWithWindows = null)
    {
        DataDirectory = dataDirectory ?? string.Empty;
        LogsDirectory = logsDirectory ?? (string.IsNullOrEmpty(dataDirectory) ? string.Empty : Path.Combine(dataDirectory, "logs"));
        AppVersion = ReadVersion();
        RuntimeVersion = Environment.Version.ToString(3);

        if (startWithWindows is { } given)
        {
            _startWithWindows = given;
        }
        else if (OperatingSystem.IsWindows())
        {
            _startWithWindows = Fortiq.Platform.Windows.WindowsAutostartController.IsAutostartEnabled();
        }
    }

    /// <summary>The version this build actually is, pre-release marker included.</summary>
    /// <remarks>
    /// The informational version, not <c>Assembly.GetName().Version</c>. The latter is a four-part
    /// number with nowhere to put "beta.1", so a beta would have shown as 0.1.0 and been
    /// indistinguishable from the release - on the one screen a person looks at to answer "which
    /// build am I running". The build stamps a commit onto the end after a '+'; that belongs in a
    /// diagnostic, not beside the product name.
    /// </remarks>
    private static string ReadVersion()
    {
        var informational = typeof(SettingsViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var build = informational.IndexOf('+', StringComparison.Ordinal);
            return build < 0 ? informational : informational[..build];
        }

        return typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
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
            StatusMessage = "The Fortiq service could not be asked how it is. " + PlainFailure.Describe(ex);
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
            StatusMessage = "That did not work. " + PlainFailure.Describe(ex);
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
