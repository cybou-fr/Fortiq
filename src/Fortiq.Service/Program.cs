using System.Runtime.Versioning;
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
    [SupportedOSPlatform("windows")]
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("The Fortiq service runs on Windows only.");
            return 1;
        }

        // Credential management runs in this binary and exits, rather than starting the host. It
        // belongs here because this is the process that reads the credentials, and it needs the same
        // state directory to find them.
        if (args.Length > 0 && args[0] == "credentials")
        {
            return await StorageCredentialCommand.RunAsync(
                args,
                FortiqStatePaths.Resolve(Environment.GetEnvironmentVariable("FORTIQ_STATE_DIRECTORY")),
                CancellationToken.None);
        }

        var doctor = args.Length > 0 && args[0] == "doctor";
        var builder = Host.CreateApplicationBuilder(doctor ? args[1..] : args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Fortiq");

        // Machine-wide by default: a service and an operator's tool have to see the same schedules
        // and the same runs.
        var paths = FortiqStatePaths.Resolve(builder.Configuration["Fortiq:StateDirectory"]);

        var engineRoot = builder.Configuration["Fortiq:EngineRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "engines");

        var storage = new FirstAvailableObjectStorageCredentials(
            new StoredObjectStorageCredentials(Path.Combine(paths.Root, "credentials")),
            new EnvironmentObjectStorageCredentialProvider());
        var readiness = new ServiceReadiness(paths, engineRoot,
            Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe"), storage);
        if (doctor)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var report = await readiness.InspectAsync(timeout.Token);
            await Console.Out.WriteLineAsync(ReadinessPublication.Serialize(report));
            return report.Passed ? 0 : 2;
        }

        if (bool.TryParse(builder.Configuration["Fortiq:ReadinessOnly"], out var readinessOnly) && readinessOnly)
        {
            // This host completes the SCM handshake, inspects access as the actual service identity,
            // writes its report, and stops. No backup, drill or retention runner is registered.
            builder.Services.AddSingleton(readiness);
            builder.Services.AddSingleton(new ReadinessOutput(
                builder.Configuration["Fortiq:ReadinessReport"] ?? Path.Combine(paths.Root, "readiness.json")));
            builder.Services.AddHostedService<ReadinessWorker>();
            await builder.Build().RunAsync();
            return 0;
        }

        var pollInterval = builder.Configuration["Fortiq:PollInterval"] is { Length: > 0 } configured
            ? TimeSpan.Parse(configured, System.Globalization.CultureInfo.InvariantCulture)
            : SchedulerOptions.Default.PollInterval;

        builder.Services.AddSingleton(new SchedulerOptions(pollInterval));
        // Credentials stored for a specific repository beat one set of environment variables that
        // covers every repository on the machine. The environment stays as a fallback for tooling,
        // tests and CI, where exporting two variables is the natural thing to do.
        builder.Services.AddSingleton<IObjectStorageCredentialProvider>(storage);
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

        builder.Services.AddSingleton(provider => new ProvenRestore(
            engineRoot,
            paths.Working,
            runDirectory: paths.Runs,
            receiptDirectory: paths.Receipts,
            storage: provider.GetRequiredService<IObjectStorageCredentialProvider>()));

        // Restore drills share the working directory, and therefore the receipt store, with backups.
        // A drill's receipt is what turns a repository from backed up into proven, and monitoring
        // reads the same directory: three components have to agree on one path or the proof is
        // written somewhere nobody looks.
        builder.Services.AddSingleton<IScheduledDrill>(provider =>
            new UnattendedRestoreDrill(provider.GetRequiredService<ProvenRestore>()));
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

        builder.Services.AddSingleton(provider => new ServiceIpcHost(
            paths,
            engineRoot,
            provider.GetRequiredService<IObjectStorageCredentialProvider>(),
            provider.GetRequiredService<IScheduleStore>(),
            provider.GetRequiredService<ProvenRestore>(),
            provider.GetRequiredService<HealthPublisher>()));

        builder.Services.AddHostedService<SchedulerWorker>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ServiceIpcHost>());

        await builder.Build().RunAsync();
        return 0;
    }
}