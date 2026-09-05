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

        var isPortable = Environment.GetCommandLineArgs().Contains("--portable", StringComparer.OrdinalIgnoreCase);
        var preferences = DesktopPreferencesStore.Resolve(!isPortable).Current;
        if (preferences.Theme == AppThemePreference.Dark)
        {
            DesignTokens.SetTheme(true);
            RequestedThemeVariant = ThemeVariant.Dark;
        }
        else if (preferences.Theme == AppThemePreference.Light)
        {
            DesignTokens.SetTheme(false);
            RequestedThemeVariant = ThemeVariant.Light;
        }
        else
        {
            RequestedThemeVariant = ThemeVariant.Default;
        }

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
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var args = desktop.Args ?? Array.Empty<string>();
            var isPortable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
            var isTrayLaunch = args.Contains("--tray", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

            var message = new Avalonia.Controls.TextBlock
            {
                Text = "Checking Fortiq installation...",
                TextWrapping = TextWrapping.Wrap
            };
            var retry = new Avalonia.Controls.Button { Content = "Retry", IsVisible = false };
            // Painted explicitly. With no background of its own the window borrowed whatever the host
            // put behind it, which on a dark system theme was a black rectangle with black text - the
            // first thing anyone sees of Fortiq, and unreadable.
            message.Foreground = DesignTokens.Ink;
            message.FontSize = 14;

            var loading = new Avalonia.Controls.Window
            {
                Title = "Fortiq",
                Width = 460,
                Height = 200,
                Background = DesignTokens.CanvasBackground,
                Icon = FortiqBrand.WindowIcon(),
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
                Content = new Avalonia.Controls.StackPanel
                {
                    Margin = new Thickness(24), Spacing = 16, Children = { message, retry }
                }
            };
            if (isTrayLaunch)
            {
                loading.Opacity = 0;
                loading.ShowInTaskbar = false;
                loading.Width = 0;
                loading.Height = 0;
                loading.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
                loading.Position = new PixelPoint(-20000, -20000);
            }

            desktop.MainWindow = loading;
            var closed = false;
            loading.Closed += (_, _) => closed = true;
            async Task InitializeAsync()
            {
                retry.IsVisible = false;
                message.Text = "Checking Fortiq installation...";
                try
                {
                    var inspector = new InstallationInspector();
                    var status = await Task.Run(() => inspector.InspectAsync());
                    if (closed) return;
                    var isProtectOnly = Environment.GetCommandLineArgs()
                        .Contains("--protect", StringComparer.OrdinalIgnoreCase);

                    if (isProtectOnly)
                    {
                        // Started by an unelevated Fortiq that needs one privileged operation. Only
                        // the wizard is shown; closing it ends this process and the unelevated window
                        // the person was already looking at refreshes.
                        var only = CreateProtectWindow();
                        desktop.MainWindow = only;
                        only.Show();
                        only.Closed += (_, _) => desktop.Shutdown();

                        // Closed here too. Every other branch falls through to the Close() below;
                        // this one returns early, so the start-up window stayed on screen forever
                        // behind the wizard.
                        loading.Close();
                        return;
                    }

                    if (isPortable || status.IsInstalled)
                    {
                        var mainWindow = CreateMainWindow(installed: status.IsInstalled && !isPortable);
                        desktop.MainWindow = mainWindow;
                        if (!isTrayLaunch)
                        {
                            mainWindow.Show();
                        }
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
                        installWindow.Show();
                    }
                    loading.Close();
                }
                catch (Exception error)
                {
                    if (closed) return;
                    if (isTrayLaunch)
                    {
                        loading.Opacity = 1;
                        loading.ShowInTaskbar = true;
                        loading.Width = 460;
                        loading.Height = 200;
                        loading.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
                    }
                    message.Text = "Fortiq could not start. " + error.Message;
                    retry.IsVisible = true;
                }
            }
            loading.Opened += async (_, _) => await InitializeAsync();
            retry.Click += async (_, _) => await InitializeAsync();

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

        // The wizard can now be given object storage keys, so something has to write them down.
        // Windows only, because the machine store is DPAPI; elsewhere the environment remains the
        // way in, and the wizard's fields simply have nowhere to be kept.
        Func<string, ObjectStorageCredentials, CancellationToken, Task>? storeCredentials = null;
        if (OperatingSystem.IsWindows())
        {
            var credentialStore = new StoredObjectStorageCredentials(Path.Combine(paths.Root, "credentials"));
            storeCredentials = credentialStore.WriteAsync;
        }

        var protect = new ProtectRepositoryAdapter(
            new RepositoryProvisioner(
                engineRoot,
                storage: storage,
                protection: new S3StorageProtectionInspector(storage)),
            paths,
            serviceClient: serviceClient,
            storeCredentials: storeCredentials);

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
        if (!installed) settings.ServiceStatus = "Portable mode";
        if (installed && OperatingSystem.IsWindows())
        {
            settings.RefreshServiceStatusAction = () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return Task.FromResult("Not supported on this platform");
                }

                // Was svcStatus.ToString(), which put the record's debug form on the Settings screen:
                // "WindowsServiceInfo { Exists = False, Running = False, CurrentState = 0,
                // ServiceSid...". Whatever a person opened this screen to find out, that was not it.
                // It also broke the running check, which compared this string to "Running".
                var svcStatus = WindowsServiceController.QueryStatus("Fortiq");

                return Task.FromResult(svcStatus switch
                {
                    { Exists: false } => "Not installed",
                    { Running: true } => "Running",
                    _ => "Stopped"
                });
            };
            settings.StartServiceAction = () =>
            {
                if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
                return Task.Run(() => OperatingSystem.IsWindows() && WindowsServiceController.StartService("Fortiq", TimeSpan.FromSeconds(5)));
            };
            settings.StopServiceAction = () =>
            {
                if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
                return Task.Run(() => OperatingSystem.IsWindows() && WindowsServiceController.StopService("Fortiq", TimeSpan.FromSeconds(5)));
            };
        }

        var tpmAvailable = OperatingSystem.IsWindows() && Fortiq.Infrastructure.Keys.WindowsTpmEnvelope.IsAvailable;
        var automaticAvailable = installed && tpmAvailable;
        var unavailableReason = !installed
            ? "Portable mode: background scheduled backups require an installed service."
            : !tpmAvailable
                ? "No security chip found. Automatic backups aren't available on this PC. You can still use Fortiq for manual recovery."
                : null;

        return new MainWindow(
            new RepositoriesViewModel(new HealthFileSource(paths.HealthReport), prove),
            () => new ProtectRepositoryViewModel(protect, automaticBackupsAvailable: automaticAvailable, automaticBackupsUnavailableReason: unavailableReason),
            settings, installed: installed,
            fileRecovery: () => new FileRecoveryViewModel(new FileRecoveryAdapter(engineRoot, paths.Runs)));
    }

    /// <summary>
    /// The wizard on its own, for the elevated pass that protecting a folder needs.
    /// </summary>
    /// <remarks>
    /// Provisioning asks the service to act with its own privileges, so the service refuses a caller
    /// who does not hold them - which left an ordinary user in installed mode unable to protect
    /// anything at all. The answer is to elevate the operation, not the application: Windows raises
    /// its prompt, this window opens, and the recovery phrase it shows never leaves the process that
    /// generated it. Running the whole desktop as administrator instead would leave a backup client
    /// with full rights on the machine for as long as it stays open, to spare one prompt.
    /// </remarks>
    private static ProtectRepositoryWindow CreateProtectWindow()
    {
        var host = CreateMainWindow(installed: true);
        var wizard = host.CreateWizard()
            ?? throw new InvalidOperationException("The protection wizard is not available in this mode.");

        return new ProtectRepositoryWindow(wizard);
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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogCrash(ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };

        if (InstallationCli.IsCliInvocation(args))
        {
            return InstallationCli.RunAsync(args).GetAwaiter().GetResult();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fortiq", "logs");
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var logPath = Path.Combine(logsDir, $"crash-{timestamp}.log");
            var content = $"""
                Fortiq Community Edition — Crash Report
                Timestamp: {DateTimeOffset.UtcNow:O}
                OS: {Environment.OSVersion}
                Runtime: {Environment.Version}
                Exception: {ex.GetType().FullName}: {ex.Message}
                StackTrace:
                {ex.StackTrace}
                """;
            File.WriteAllText(logPath, content);
        }
        catch
        {
            // Do not fail in crash logger
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<FortiqApplication>()
            .UsePlatformDetect()
            .LogToTrace();
}
