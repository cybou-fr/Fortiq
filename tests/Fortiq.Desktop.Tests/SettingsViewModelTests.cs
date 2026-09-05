using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void DefaultsAreProperlyInitialized()
    {
        var vm = new SettingsViewModel(@"C:\ProgramData\Fortiq");

        Assert.Equal(@"C:\ProgramData\Fortiq", vm.DataDirectory);
        Assert.Equal(@"C:\ProgramData\Fortiq\logs", vm.LogsDirectory);
        Assert.Equal(AppThemePreference.System, vm.ThemePreference);
        Assert.False(vm.IsServiceRunning);
        Assert.False(vm.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(vm.AppVersion));
        Assert.False(string.IsNullOrWhiteSpace(vm.RuntimeVersion));
    }

    [Fact]
    public void ThemePreferenceChangeFiresNotificationAndEvent()
    {
        var vm = new SettingsViewModel(@"C:\ProgramData\Fortiq");
        AppThemePreference? firedPreference = null;
        var propertyChangedFired = false;

        vm.ThemeChanged += pref => firedPreference = pref;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ThemePreference))
            {
                propertyChangedFired = true;
            }
        };

        vm.ThemePreference = AppThemePreference.Dark;

        Assert.Equal(AppThemePreference.Dark, vm.ThemePreference);
        Assert.Equal(AppThemePreference.Dark, firedPreference);
        Assert.True(propertyChangedFired);
    }

    [Fact]
    public async Task RefreshServiceStatusUpdatesRunningState()
    {
        var vm = new SettingsViewModel(@"C:\ProgramData\Fortiq")
        {
            RefreshServiceStatusAction = () => Task.FromResult("Running")
        };

        await vm.RefreshServiceStatusAsync();

        Assert.Equal("Running", vm.ServiceStatus);
        Assert.True(vm.IsServiceRunning);
    }

    [Fact]
    public async Task ToggleServiceSwitchesState()
    {
        var startCalled = false;
        var stopCalled = false;
        var currentStatus = "Stopped";

        var vm = new SettingsViewModel(@"C:\ProgramData\Fortiq")
        {
            RefreshServiceStatusAction = () => Task.FromResult(currentStatus),
            StartServiceAction = () =>
            {
                startCalled = true;
                currentStatus = "Running";
                return Task.FromResult(true);
            },
            StopServiceAction = () =>
            {
                stopCalled = true;
                currentStatus = "Stopped";
                return Task.FromResult(true);
            }
        };

        await vm.RefreshServiceStatusAsync();
        Assert.False(vm.IsServiceRunning);

        // Toggle to start
        await vm.ToggleServiceAsync();
        Assert.True(startCalled);
        Assert.Equal("Running", vm.ServiceStatus);
        Assert.True(vm.IsServiceRunning);

        // Toggle to stop
        await vm.ToggleServiceAsync();
        Assert.True(stopCalled);
        Assert.Equal("Stopped", vm.ServiceStatus);
        Assert.False(vm.IsServiceRunning);
    }

    [Fact]
    public void StartWithWindowsRollsBackWhenActionReturnsFalse()
    {
        // The starting state is stated, not read from this machine. Without it the test passed only
        // where Fortiq's autostart happened to be off, and installing Fortiq turned it red.
        var vm = new SettingsViewModel(@"C:\ProgramData\Fortiq", startWithWindows: false)
        {
            SetAutostartAction = _ => false
        };

        vm.StartWithWindows = true;

        Assert.False(vm.StartWithWindows);
        Assert.Contains("Could not update Windows startup registration", vm.StatusMessage);
    }
}
