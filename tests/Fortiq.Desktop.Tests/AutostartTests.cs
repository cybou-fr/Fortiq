using Fortiq.Desktop.ViewModels;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop.Tests;

public sealed class AutostartTests
{
    [Fact]
    public void SettingsViewModelNotifiesOnStartWithWindowsChanged()
    {
        // Stated rather than read from this machine, so the result does not depend on whether the
        // developer happens to have Fortiq set to start with Windows.
        var vm = new SettingsViewModel(
            Path.Combine(Path.GetTempPath(), "fortiq-test-" + Guid.NewGuid().ToString("N")),
            startWithWindows: false);
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
