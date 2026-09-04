using Fortiq.Infrastructure.ObjectStorage;
using Fortiq.Operations;
using Fortiq.Application;
using Fortiq.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Fortiq.Service;

/// <summary>
/// The Fortiq service: it runs scheduled backups and nothing else. It holds no recovery material,
/// and every repository it opens is opened by that machine's own device-bound key.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("The Fortiq service runs on Windows only.");
            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Fortiq");

        // Machine-wide by default: a service and an operator's tool have to see the same schedules
        // and the same runs.
        var paths = FortiqStatePaths.Resolve(builder.Configuration["Fortiq:StateDirectory"]);

        var engineRoot = builder.Configuration["Fortiq:EngineRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "engines");

        var pollInterval = builder.Configuration["Fortiq:PollInterval"] is { Length: > 0 } configured
            ? TimeSpan.Parse(configured, System.Globalization.CultureInfo.InvariantCulture)
            : SchedulerOptions.Default.PollInterval;

        builder.Services.AddSingleton(new SchedulerOptions(pollInterval));
        builder.Services.AddSingleton<IObjectStorageCredentialProvider, EnvironmentObjectStorageCredentialProvider>();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IScheduleStore>(new FileSystemScheduleStore(paths.Schedules));
        builder.Services.AddSingleton<IScheduledBackup>(provider =>
            new UnattendedBackup(
                engineRoot,
                paths.Working,
                runDirectory: paths.Runs,
                receiptDirectory: paths.Receipts,
                storage: provider.GetRequiredService<IObjectStorageCredentialProvider>()));
        builder.Services.AddSingleton(provider => new ScheduledBackupRunner(
            provider.GetRequiredService<IScheduleStore>(),
            provider.GetRequiredService<IScheduledBackup>()));

        // Restore drills share the working directory, and therefore the receipt store, with backups.
        // A drill's receipt is what turns a repository from backed up into proven, and monitoring
        // reads the same directory: three components have to agree on one path or the proof is
        // written somewhere nobody looks.
        builder.Services.AddSingleton<IScheduledDrill>(provider =>
            new UnattendedRestoreDrill(new ProvenRestore(
                engineRoot,
                paths.Working,
                runDirectory: paths.Runs,
                receiptDirectory: paths.Receipts,
                storage: provider.GetRequiredService<IObjectStorageCredentialProvider>())));
        // Retention is the only scheduled operation that destroys anything, and it is opt-in per
        // schedule: a schedule file that says nothing about retention keeps everything forever.
        builder.Services.AddSingleton<IScheduledRetention>(provider =>
            new UnattendedRetention(
                engineRoot,
                paths.Working,
                runDirectory: paths.Runs,
                receiptDirectory: paths.Receipts,
                storage: provider.GetRequiredService<IObjectStorageCredentialProvider>()));
        builder.Services.AddSingleton(provider => new ScheduledRetentionRunner(
            provider.GetRequiredService<IScheduleStore>(),
            provider.GetRequiredService<IScheduledRetention>()));

        builder.Services.AddSingleton(provider => new ScheduledDrillRunner(
            provider.GetRequiredService<IScheduleStore>(),
            provider.GetRequiredService<IScheduledDrill>()));

        // Health is published to files rather than served: a monitoring path that depends on this
        // service being reachable reports health right up until it cannot report at all.
        builder.Services.AddSingleton(provider => new HealthPublisher(
            provider.GetRequiredService<IScheduleStore>(),
            paths.Receipts,
            paths.HealthReport,
            paths.HealthMetrics,
            protection: new S3StorageProtectionInspector(
                provider.GetRequiredService<IObjectStorageCredentialProvider>())));
        builder.Services.AddHostedService<SchedulerWorker>();

        await builder.Build().RunAsync();
        return 0;
    }
}
