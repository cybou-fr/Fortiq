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
        var stateDirectory = builder.Configuration["Fortiq:StateDirectory"]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Fortiq");

        var engineRoot = builder.Configuration["Fortiq:EngineRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "engines");

        var pollInterval = builder.Configuration["Fortiq:PollInterval"] is { Length: > 0 } configured
            ? TimeSpan.Parse(configured, System.Globalization.CultureInfo.InvariantCulture)
            : SchedulerOptions.Default.PollInterval;

        builder.Services.AddSingleton(new SchedulerOptions(pollInterval));
        builder.Services.AddSingleton<IObjectStorageCredentialProvider, EnvironmentObjectStorageCredentialProvider>();
        builder.Services.AddSingleton<IScheduleStore>(new FileSystemScheduleStore(stateDirectory));
        builder.Services.AddSingleton<IScheduledBackup>(provider =>
            new UnattendedBackup(
                engineRoot,
                Path.Combine(stateDirectory, "work"),
                storage: provider.GetRequiredService<IObjectStorageCredentialProvider>()));
        builder.Services.AddSingleton(provider => new ScheduledBackupRunner(
            provider.GetRequiredService<IScheduleStore>(),
            provider.GetRequiredService<IScheduledBackup>()));

        // Health is published to files rather than served: a monitoring path that depends on this
        // service being reachable reports health right up until it cannot report at all.
        builder.Services.AddSingleton(provider => new HealthPublisher(
            provider.GetRequiredService<IScheduleStore>(),
            Path.Combine(stateDirectory, "work", "receipts"),
            Path.Combine(stateDirectory, "health", "health.json"),
            Path.Combine(stateDirectory, "health", "fortiq.prom")));
        builder.Services.AddHostedService<SchedulerWorker>();

        await builder.Build().RunAsync();
        return 0;
    }
}
