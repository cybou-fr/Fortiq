using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Fortiq.Service;

public sealed record ReadinessOutput(string Path);

public static class ReadinessPublication
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Serialize(ServiceReadinessReport report) => JsonSerializer.Serialize(report, Options);

    public static async Task WriteAsync(ServiceReadinessReport report, string path, CancellationToken cancellationToken)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(temporary, Serialize(report), cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>Runs preflight inside the real service host and exits without executing scheduled work.</summary>
[SupportedOSPlatform("windows")]
public sealed class ReadinessWorker(ServiceReadiness readiness, ReadinessOutput output, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var report = await readiness.InspectAsync(timeout.Token);
            await ReadinessPublication.WriteAsync(report, output.Path, stoppingToken);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
