using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

public sealed class FortiqApplication : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var stateDirectory = Environment.GetEnvironmentVariable("FORTIQ_STATE_DIRECTORY")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Fortiq");

            var engineRoot = ResolveEngineRoot();

            var protect = new ProtectRepositoryAdapter(
                new RepositoryProvisioner(engineRoot),
                stateDirectory);

            var reportPath = Path.Combine(stateDirectory, "health", "health.json");
            var schedules = new FileSystemScheduleStore(stateDirectory);
            var receipts = Path.Combine(stateDirectory, "receipts");

            var prove = new ProveRecoveryAdapter(
                schedules,
                new ProvenRestore(engineRoot, stateDirectory, receiptDirectory: receipts),
                new HealthPublisher(
                    schedules,
                    receipts,
                    reportPath,
                    Path.Combine(stateDirectory, "health", "fortiq.prom")));

            desktop.MainWindow = new MainWindow(
                new RepositoriesViewModel(new HealthFileSource(reportPath), prove),
                () => new ProtectRepositoryViewModel(protect));
        }

        base.OnFrameworkInitializationCompleted();
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

    public async Task<HealthReport> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException(
                $"No health report at {_path}. The Fortiq service writes one after each pass.",
                _path);
        }

        var document = JsonSerializer.Deserialize<ReportDocument>(
            await File.ReadAllTextAsync(_path, cancellationToken),
            Options) ?? throw new InvalidDataException("The health report is empty.");

        return document.Schema == HealthReport.Schema && document.Version == HealthReport.SchemaVersion
            ? new HealthReport(document.ProducedAt, document.Repositories)
            : throw new InvalidDataException("Unsupported health report schema or version.");
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
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<FortiqApplication>()
            .UsePlatformDetect()
            .LogToTrace();
}
