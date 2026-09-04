using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using Fortiq.Provisioning;

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

            var engineRoot = Environment.GetEnvironmentVariable("FORTIQ_ENGINE_ROOT")
                ?? Path.Combine(AppContext.BaseDirectory, "engine");

            var protect = new ProtectRepositoryAdapter(
                new RepositoryProvisioner(engineRoot),
                stateDirectory);

            desktop.MainWindow = new MainWindow(
                new RepositoriesViewModel(
                    new HealthFileSource(Path.Combine(stateDirectory, "health", "health.json")),
                    new RecoveryNotWiredUp()),
                () => new ProtectRepositoryViewModel(protect));
        }

        base.OnFrameworkInitializationCompleted();
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

/// <summary>
/// Proving recovery means restoring, which needs the kit and the engine. Until the desktop is given
/// those, it says so instead of pretending the button did something.
/// </summary>
public sealed class RecoveryNotWiredUp : IProveRecovery
{
    public Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken) =>
        Task.FromException<bool>(new NotSupportedException(
            "Proving recovery from the desktop is not connected yet; run a restore with Fortiq.Recover."));
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
