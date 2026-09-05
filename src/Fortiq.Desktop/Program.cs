using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Fortiq.Application;
using Fortiq.Desktop.ViewModels;
using Fortiq.Infrastructure.ObjectStorage;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Platform.Windows;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

public sealed class FortiqApplication : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;

        // Fluent paints checkboxes, radio buttons and focus rings with the accent colour Windows
        // personalisation happens to be set to. On this machine that is magenta, so the setup screen
        // had magenta checkboxes beside a blue primary button - two accents, neither of them Fortiq's.
        // Pinning the accent to the brand colour makes one product instead of a theme sampler.
        var brand = Color.Parse("#2563EB");
        Resources["SystemAccentColor"] = brand;
        Resources["SystemAccentColorLight1"] = Color.Parse("#3B82F6");
        Resources["SystemAccentColorLight2"] = Color.Parse("#60A5FA");
        Resources["SystemAccentColorLight3"] = Color.Parse("#93C5FD");
        Resources["SystemAccentColorDark1"] = Color.Parse("#1D4ED8");
        Resources["SystemAccentColorDark2"] = Color.Parse("#1E40AF");
        Resources["SystemAccentColorDark3"] = Color.Parse("#1E3A8A");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            var isPortable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase);

            var inspector = new InstallationInspector();

            // Run off the UI thread, and block here rather than on the dispatcher. Calling
            // GetResult() directly on the UI thread deadlocked: the inspector's continuations were
            // posted back to the thread that was blocking on them, and the application started with
            // no window - a process alive, nothing on screen, nothing in the log.
            //
            // Blocking at all is still wrong for a slow disk; the inspection hashes a 31 MB engine
            // binary. That is a separate change: show the window first and fill it in.
            var status = Task.Run(() => inspector.InspectAsync()).GetAwaiter().GetResult();

            if (isPortable || status.IsInstalled)
            {
                desktop.MainWindow = CreateMainWindow(installed: status.IsInstalled && !isPortable);
            }
            else
            {
                var installOperations = new InstallationManager();
                var installVm = new InstallViewModel(inspector, status, installOperations);
                var installWindow = new InstallWindow(installVm);

                installVm.RequestCloseAndLaunchMain += () =>
                {
                    var mainWindow = CreateMainWindow(installed: true);
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    installWindow.Close();
                };

                installVm.RequestCloseAndLaunchPortable += () =>
                {
                    var mainWindow = CreateMainWindow(installed: false);
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    installWindow.Close();
                };

                desktop.MainWindow = installWindow;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Composes the client for one mode. The two are separate, not one with a fallback.
    /// </summary>
    /// <param name="installed">
    /// True for an installed machine, where a service holds the privileges and the desktop asks it to
    /// act. False for portable, where there is no service and the desktop acts for itself in its own
    /// state directory under a user-scoped key.
    /// </param>
    /// <remarks>
    /// Mixing the two produces the two failures worth naming. A portable client that finds an
    /// installed service starts driving privileged operations it was never meant to reach; an
    /// installed client that finds its service down quietly does the work itself, with a user-scoped
    /// key and a schedule written by whoever is logged in. Both are the boundary holding only while
    /// nothing goes wrong.
    /// </remarks>
    private static MainWindow CreateMainWindow(bool installed)
    {
        // Asked for, never composed. The desktop and the service have to mean the same
        // directory by "receipts", or a restore proven here vanishes from the report the
        // service publishes next.
        //
        // Portable carries its state beside the executable, which is what portable means: a stick a
        // recovery engineer walks to a machine with. It also must not write into the installed
        // machine's %ProgramData%, where it has read access and nothing more.
        var paths = installed
            ? FortiqStatePaths.Resolve()
            : FortiqStatePaths.Resolve(Path.Combine(AppContext.BaseDirectory, "portable-state"));

        var engineRoot = ResolveEngineRoot();
        // The same order the service uses, so both processes resolve one repository's storage
        // identity the same way. The stored half is Windows-only, and on any other platform the
        // desktop falls back to the environment rather than refusing to start.
        IObjectStorageCredentialProvider storage = OperatingSystem.IsWindows()
            ? new FirstAvailableObjectStorageCredentials(
                new StoredObjectStorageCredentials(Path.Combine(paths.Root, "credentials")),
                new EnvironmentObjectStorageCredentialProvider())
            : new EnvironmentObjectStorageCredentialProvider();

        // Null in portable mode, and that is the whole of the mode's enforcement: with no client
        // there is no path from this process to the privileged service.
        var serviceClient = installed && OperatingSystem.IsWindows() ? new ServiceIpcClient() : null;

        var protect = new ProtectRepositoryAdapter(
            new RepositoryProvisioner(
                engineRoot,
                storage: storage,
                protection: new S3StorageProtectionInspector(storage)),
            paths,
            serviceClient: serviceClient);

        var schedules = new FileSystemScheduleStore(paths.Schedules);
        var prove = new ProveRecoveryAdapter(
            schedules,
            new ProvenRestore(
                engineRoot,
                paths.Working,
                runDirectory: paths.Runs,
                receiptDirectory: paths.Receipts,
                storage: storage),
            new HealthPublisher(
                schedules,
                paths.Receipts,
                paths.HealthReport,
                paths.HealthMetrics,
                protection: new S3StorageProtectionInspector(storage)),
            serviceClient: serviceClient);

        var settings = new SettingsViewModel(paths.Root, Path.Combine(paths.Root, "logs"));
        if (OperatingSystem.IsWindows())
        {
            settings.RefreshServiceStatusAction = () =>
            {
                if (!OperatingSystem.IsWindows()) return Task.FromResult("Not Supported");
                var svcStatus = WindowsServiceController.QueryStatus("Fortiq");
                return Task.FromResult(svcStatus.ToString());
            };
            settings.StartServiceAction = () =>
            {
                if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
                return Task.FromResult(WindowsServiceController.StartService("Fortiq", TimeSpan.FromSeconds(5)));
            };
            settings.StopServiceAction = () =>
            {
                if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
                return Task.FromResult(WindowsServiceController.StopService("Fortiq", TimeSpan.FromSeconds(5)));
            };
        }

        return new MainWindow(
            new RepositoriesViewModel(new HealthFileSource(paths.HealthReport), prove),
            () => new ProtectRepositoryViewModel(protect),
            settings);
    }

    private static string ResolveEngineRoot()
    {
        if (Environment.GetEnvironmentVariable("FORTIQ_ENGINE_ROOT") is { Length: > 0 } configured && Directory.Exists(configured))
        {
            return configured;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "engines");
        if (File.Exists(Path.Combine(candidate, "manifest.json")))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var enginesPath = Path.Combine(directory.FullName, "engines");
            if (File.Exists(Path.Combine(enginesPath, "manifest.json")))
            {
                return enginesPath;
            }

            directory = directory.Parent;
        }

        return candidate;
    }
}

/// <summary>
/// Reads the health report the service publishes. The desktop deliberately reads the same file a
/// monitoring system would: what a person sees and what an alert fires on are then the same thing.
/// </summary>
public sealed class HealthFileSource : IHealthSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    public HealthFileSource(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public async Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new HealthReadResult(HealthStoreState.NotInitialized);
        }
        try
        {
            var document = JsonSerializer.Deserialize<ReportDocument>(
                await File.ReadAllTextAsync(_path, cancellationToken),
                Options) ?? throw new InvalidDataException("The health report is empty.");

            if (document.Schema != HealthReport.Schema || document.Version != HealthReport.SchemaVersion)
            {
                throw new InvalidDataException("Unsupported health report schema or version.");
            }

            var report = new HealthReport(document.ProducedAt, document.Repositories);
            return new HealthReadResult(
                report.Repositories.Count == 0 ? HealthStoreState.Empty : HealthStoreState.Active,
                report);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return new HealthReadResult(HealthStoreState.Corrupt, Detail: error.Message);
        }
    }

    private sealed record ReportDocument(
        string Schema,
        int Version,
        DateTimeOffset ProducedAt,
        IReadOnlyList<RepositoryHealth> Repositories);
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (InstallationCli.IsCliInvocation(args))
        {
            return InstallationCli.RunAsync(args).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<FortiqApplication>()
            .UsePlatformDetect()
            .LogToTrace();
}
