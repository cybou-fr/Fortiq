using System;
using System.IO;
using Fortiq.Desktop;
using Fortiq.Desktop.ViewModels;
using Xunit;

namespace Fortiq.Desktop.Tests;

public sealed class DesktopPreferencesTests : IDisposable
{
    private readonly string _tempDir;

    public DesktopPreferencesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fortiq-pref-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void LoadReturnsDefaultsWhenFileDoesNotExist()
    {
        var store = new DesktopPreferencesStore(_tempDir);

        Assert.Equal(AppThemePreference.System, store.Current.Theme);
        Assert.False(store.Current.StartWithWindows);
        Assert.True(store.Current.MinimizeToTrayOnClose);
    }

    [Fact]
    public void SaveAndLoadRoundtripsSuccessfully()
    {
        var store = new DesktopPreferencesStore(_tempDir);
        var custom = new DesktopPreferences
        {
            Theme = AppThemePreference.Dark,
            StartWithWindows = true,
            MinimizeToTrayOnClose = false
        };

        store.Save(custom);

        var loaded = store.Load();
        Assert.Equal(AppThemePreference.Dark, loaded.Theme);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.MinimizeToTrayOnClose);
        Assert.Equal(loaded, store.Current);
    }

    [Fact]
    public void LoadReturnsDefaultsWhenFileIsCorrupted()
    {
        var filePath = Path.Combine(_tempDir, "preferences.json");
        File.WriteAllText(filePath, "{ corrupt json ... invalid content !!!");

        var store = new DesktopPreferencesStore(_tempDir);

        Assert.Equal(AppThemePreference.System, store.Current.Theme);
        Assert.False(store.Current.StartWithWindows);
        Assert.True(store.Current.MinimizeToTrayOnClose);
    }

    [Fact]
    public void UpdateThemePersistsNewTheme()
    {
        var store = new DesktopPreferencesStore(_tempDir);

        store.UpdateTheme(AppThemePreference.Light);

        Assert.Equal(AppThemePreference.Light, store.Current.Theme);

        // Verify fresh reload from disk
        var reloaded = store.Load();
        Assert.Equal(AppThemePreference.Light, reloaded.Theme);
    }

    [Fact]
    public void UpdateStartWithWindowsPersistsValue()
    {
        var store = new DesktopPreferencesStore(_tempDir);

        store.UpdateStartWithWindows(true);

        Assert.True(store.Current.StartWithWindows);
        Assert.True(store.Load().StartWithWindows);
    }

    [Fact]
    public void UpdateMinimizeToTrayPersistsValue()
    {
        var store = new DesktopPreferencesStore(_tempDir);

        store.UpdateMinimizeToTray(false);

        Assert.False(store.Current.MinimizeToTrayOnClose);
        Assert.False(store.Load().MinimizeToTrayOnClose);
    }

    [Fact]
    public void ResolveReturnsStoreForInstalledAndPortable()
    {
        var installedStore = DesktopPreferencesStore.Resolve(installed: true);
        var portableStore = DesktopPreferencesStore.Resolve(installed: false);

        Assert.NotNull(installedStore);
        Assert.NotNull(portableStore);
    }
}
