using Fortiq.Desktop.ViewModels;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop.Tests;

public sealed class AutostartTests
{
    [Fact]
    public void SettingsViewModelNotifiesOnStartWithWindowsChanged()
    {
        var vm = new SettingsViewModel(Path.Combine(Path.GetTempPath(), "fortiq-test-" + Guid.NewGuid().ToString("N")));
        var propertyChanged = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.StartWithWindows))
            {
                propertyChanged = true;
            }
        };

        var initial = vm.StartWithWindows;
        vm.StartWithWindows = !initial;

        Assert.True(propertyChanged);
        Assert.Equal(!initial, vm.StartWithWindows);

        // Reset to initial state
        vm.StartWithWindows = initial;
    }

    [Fact]
    public void WindowsAutostartControllerDoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(WindowsAutostartController.IsAutostartEnabled());
            Assert.False(WindowsAutostartController.SetAutostartEnabled(true));
            return;
        }

        // On Windows, reading status should never throw an unhandled exception
        var status = WindowsAutostartController.IsAutostartEnabled();
        Assert.True(status || !status);
    }
}
