using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Diagnostics;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Service;

namespace Fortiq.Recovery.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class ServiceReadinessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fortiq-readiness-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingInstallationIsReportedWithoutCreatingStateOrClaimingReadiness()
    {
        var report = await Inspector().InspectAsync(CancellationToken.None);
        Assert.False(report.Passed);
        Assert.False(Directory.Exists(_root));
        Assert.Contains(report.Findings, finding => finding.Check == "engine" && !finding.Passed);
        Assert.Contains(report.Findings, finding => finding.Check == "active-schedules" && !finding.Passed);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User!.Value, report.AccountSid);
    }

    [Fact]
    public async Task WritableStateProbesLeaveNoFilesAndNoScheduleIsNotReady()
    {
        PrepareState();
        var report = await Inspector().InspectAsync(CancellationToken.None);
        Assert.All(report.Findings.Where(finding => finding.Check == "state-access"), finding => Assert.True(finding.Passed));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task ARecoveryOnlyKitCannotBeMistakenForAnUnattendedSetup()
    {
        PrepareState();
        var kitPath = Path.Combine(_root, "kit");
        using var lease = new BufferKeyLease(new byte[32]);
        await RecoveryKitStore.WriteAsync(kitPath, "repository", new RecoveryKitEngine("restic", "0.19.1", new string('a', 64)),
            [Bip39RecoveryEnvelope.Wrap(new byte[32], Bip39Mnemonic.Create(), lease)], null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "schedules", "documents.json"), JsonSerializer.Serialize(new
        {
            schema = "fortiq.backup-schedule", version = 1, id = "documents", repository = "repository", kit = kitPath,
            source = _root, sourceStableId = "documents", recurrence = new { kind = "interval", period = "06:00:00" }
        }));
        await File.WriteAllTextAsync(Path.Combine(_root, "schedules", "broken.json"), "{");
        var report = await Inspector().InspectAsync(CancellationToken.None);
        Assert.False(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Check == "schedule-format" && !finding.Passed);
        Assert.Contains(report.Findings, finding => finding.Check == "recovery-kit" && finding.Passed);
        Assert.Contains(report.Findings, finding => finding.Check == "device-key" && !finding.Passed);
        Assert.DoesNotContain(report.Findings, finding => finding.Check == "repository-access" && finding.Passed);
    }

    [Fact]
    public async Task ReadinessPublicationReplacesTheWholeReportAndIncludesItsIdentityAndScope()
    {
        PrepareState();
        var report = await Inspector().InspectAsync(CancellationToken.None);
        var path = Path.Combine(_root, "readiness.json");
        await File.WriteAllTextAsync(path, "old report");
        await ReadinessPublication.WriteAsync(report, path, CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("fortiq.service-readiness", document.RootElement.GetProperty("schema").GetString());
        Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(report.AccountSid, document.RootElement.GetProperty("accountSid").GetString());
        Assert.Contains("not a backup", document.RootElement.GetProperty("scope").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    private ServiceReadiness Inspector() => new(FortiqStatePaths.Resolve(_root), Path.Combine(_root, "engine"),
        Path.Combine(_root, "helper.exe"), new NoObjectStorageCredentials());

    [Fact]
    public async Task ReadinessOnlyHostPublishesItsFailureAndExitsWithoutRunningBackups()
    {
        PrepareState();
        var reportPath = Path.Combine(_root, "readiness.json");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Fortiq.Service.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _root
        };
        foreach (var argument in new[] { "--Fortiq:ReadinessOnly", "true", "--Fortiq:StateDirectory", _root,
            "--Fortiq:EngineRoot", Path.Combine(_root, "missing-engine"), "--Fortiq:ReadinessReport", reportPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("Fortiq scheduler started", await output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, await error);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.False(report.RootElement.GetProperty("passed").GetBoolean());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "work"), "*", SearchOption.AllDirectories));
    }

    private void PrepareState()
    {
        foreach (var child in new[] { "schedules", "state", "work", "work/receipts", "runs", "health" })
            Directory.CreateDirectory(Path.Combine(_root, child));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
