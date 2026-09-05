using Fortiq.Desktop.ViewModels;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop.Tests;

public sealed class InstallViewModelTests
{
    private sealed class FakeInspector : IInstallationInspector
    {
        public SystemInstallationStatus ExpectedStatus { get; set; } = new(
            IsInstalled: false,
            InstallationPath: null,
            ExecutablePath: @"C:\Tools\Fortiq\Fortiq.Desktop.exe",
            CurrentVersion: new Version(1, 0, 0),
            Service: new ServiceComponentStatus(false, false, null, null, null),
            Engine: new EngineComponentStatus("restic", "0.19.1", "0.19.1", true, @"C:\Tools\Fortiq\engines\restic.exe"),
            PasswordHelper: new HelperComponentStatus(true, true, @"C:\Tools\Fortiq\Fortiq.PasswordHelper.exe"),
            Platform: new PlatformPrerequisitesStatus(true, false, true, "10.0.0"),
            Findings: Array.Empty<InstallationFinding>());

        public Task<SystemInstallationStatus> InspectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ExpectedStatus);
    }

    private sealed class FakeOperations : IInstallationOperations
    {
        public int ExitCode { get; set; }
        public bool WasCalled { get; private set; }
        public bool AutoStartPassed { get; private set; }

        public Task<int> ExecuteInstallAsync(
            string targetDir,
            bool installService,
            bool addToPath,
            bool autoStartOnLogon,
            IProgress<(string Message, double Percent)> progress,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            AutoStartPassed = autoStartOnLogon;
            progress.Report(("Installing...", 50));
            return Task.FromResult(ExitCode);
        }
    }

    [Fact]
    public void InitialStateMatchesPrerequisites()
    {
        var inspector = new FakeInspector();
        var vm = new InstallViewModel(inspector, inspector.ExpectedStatus);

        Assert.True(vm.PrerequisitesMet);
        Assert.True(vm.CanInstall);
        Assert.True(vm.CanRunPortable);
        Assert.True(vm.TpmAvailable);
        Assert.True(vm.RuntimeValid);
        Assert.True(vm.EngineVerified);
        Assert.False(vm.VssAvailable);
        Assert.True(vm.InstallService);
        Assert.True(vm.AddToPath);
        Assert.True(vm.AutoStartOnLogon);
        Assert.False(string.IsNullOrWhiteSpace(vm.InstallDirectory));
    }

    [Fact]
    public void PrerequisitesFailWhenEngineNotVerified()
    {
        var inspector = new FakeInspector
        {
            ExpectedStatus = new(
                IsInstalled: false,
                InstallationPath: null,
                ExecutablePath: @"C:\Tools\Fortiq\Fortiq.Desktop.exe",
                CurrentVersion: new Version(1, 0, 0),
                Service: new ServiceComponentStatus(false, false, null, null, null),
                Engine: new EngineComponentStatus("restic", "0.19.1", null, false, @"C:\Tools\Fortiq\engines\restic.exe"),
                PasswordHelper: new HelperComponentStatus(true, true, @"C:\Tools\Fortiq\Fortiq.PasswordHelper.exe"),
                Platform: new PlatformPrerequisitesStatus(true, false, true, "10.0.0"),
                Findings: Array.Empty<InstallationFinding>())
        };
        var vm = new InstallViewModel(inspector, inspector.ExpectedStatus);

        Assert.False(vm.EngineVerified);
        Assert.False(vm.PrerequisitesMet);
        Assert.False(vm.CanInstall);
        Assert.True(vm.CanRunPortable);
    }

    [Fact]
    public void RunPortableFiresRequestCloseAndLaunchPortableEvent()
    {
        var inspector = new FakeInspector();
        var vm = new InstallViewModel(inspector, inspector.ExpectedStatus);

        var eventFired = false;
        vm.RequestCloseAndLaunchPortable += () => eventFired = true;

        vm.RunPortable();

        Assert.True(eventFired);
    }

    [Fact]
    public async Task InstallAsyncInvokesOperationsAndFiresRequestCloseAndLaunchMainOnSuccess()
    {
        var inspector = new FakeInspector();
        var operations = new FakeOperations { ExitCode = 0 };
        var vm = new InstallViewModel(inspector, inspector.ExpectedStatus, operations);

        var mainFired = false;
        vm.RequestCloseAndLaunchMain += () => mainFired = true;

        await vm.InstallAsync();

        Assert.True(operations.WasCalled);
        Assert.True(operations.AutoStartPassed);
        Assert.True(mainFired);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsyncSetsErrorMessageWhenElevationDeclined()
    {
        var inspector = new FakeInspector();
        var operations = new FakeOperations { ExitCode = 66 }; // UAC declined
        var vm = new InstallViewModel(inspector, inspector.ExpectedStatus, operations);

        var mainFired = false;
        vm.RequestCloseAndLaunchMain += () => mainFired = true;

        await vm.InstallAsync();

        Assert.True(operations.WasCalled);
        Assert.False(mainFired);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("declined", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
