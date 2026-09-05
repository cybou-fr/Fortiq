using System.IO;
using System.Text.Json;
using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop;

public sealed record DesktopPreferences
{
    public AppThemePreference Theme { get; init; } = AppThemePreference.System;
    public bool StartWithWindows { get; init; }
    public bool MinimizeToTrayOnClose { get; init; } = true;
}

/// <summary>
/// Thread-safe atomic persistence store for user desktop preferences.
/// </summary>
public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly object _lock = new();

    public DesktopPreferences Current { get; private set; }

    public DesktopPreferencesStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _filePath = Path.Combine(stateDirectory, "preferences.json");
        Current = Load();
    }

    public static DesktopPreferencesStore Resolve(bool installed)
    {
        var dir = installed
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fortiq")
            : Path.Combine(AppContext.BaseDirectory, "portable-state");

        return new DesktopPreferencesStore(dir);
    }

    public DesktopPreferences Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return new DesktopPreferences();
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<DesktopPreferences>(json);
                return loaded ?? new DesktopPreferences();
            }
            catch
            {
                return new DesktopPreferences();
            }
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_lock)
        {
            Current = preferences;
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var tempFile = _filePath + $".tmp.{Guid.NewGuid():N}";
                var json = JsonSerializer.Serialize(preferences, SerializerOptions);
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, _filePath, overwrite: true);
            }
            catch
            {
                // Persistence failures should never crash the desktop application
            }
        }
    }

    public void UpdateTheme(AppThemePreference theme)
    {
        Save(Current with { Theme = theme });
    }

    public void UpdateStartWithWindows(bool startWithWindows)
    {
        Save(Current with { StartWithWindows = startWithWindows });
    }

    public void UpdateMinimizeToTray(bool minimize)
    {
        Save(Current with { MinimizeToTrayOnClose = minimize });
    }
}
