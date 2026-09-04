using Fortiq.Desktop;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using Fortiq.Scheduling;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The desktop reads the file the service writes. These two are written in different projects, so
/// the only thing that keeps them agreeing is a test that writes with one and reads with the other.
/// </summary>
public sealed class HealthFileSourceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fortiq-desktop-").FullName;

    [Fact]
    public async Task TheScreenReadsWhatTheServiceWrote()
    {
        var now = DateTimeOffset.UtcNow;
        var facts = new RepositoryFacts("a", "documents", now.AddHours(-1), null, null, KitPresent: true, StorageImmutable: true);
        var report = new HealthReport(now, [HealthAssessor.Assess(facts, now)]);

        var path = Path.Combine(_directory, "health.json");
        await HealthPublication.WriteJsonAsync(report, path, CancellationToken.None);

        var read = await new HealthFileSource(path).ReadAsync(CancellationToken.None);

        Assert.Equal(HealthStoreState.Active, read.State);
        var repository = Assert.Single(read.Report!.Repositories);
        Assert.Equal(HealthVerdict.Unproven, repository.Verdict);
        Assert.Equal("documents", repository.ScheduleId);
        Assert.Equal(facts.LastBackupAt, repository.Facts.LastBackupAt);
        Assert.Contains(repository.Findings, finding => finding.Code == "restore-never-proven");
    }

    [Fact]
    public async Task AMissingReportIsAFirstRunState()
    {
        var path = Path.Combine(_directory, "absent.json");

        var read = await new HealthFileSource(path).ReadAsync(CancellationToken.None);

        Assert.Equal(HealthStoreState.NotInitialized, read.State);
        Assert.Null(read.Report);
    }

    [Fact]
    public async Task AReportFromAnotherSchemaIsRefusedRatherThanGuessedAt()
    {
        var path = Path.Combine(_directory, "health.json");
        await File.WriteAllTextAsync(
            path,
            """{"schema":"something.else","version":1,"producedAt":"2026-09-04T00:00:00+00:00","repositories":[]}""");

        var read = await new HealthFileSource(path).ReadAsync(CancellationToken.None);

        Assert.Equal(HealthStoreState.Corrupt, read.State);
        Assert.Contains("Unsupported", read.Detail, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

/// <summary>
/// The wizard writes a schedule file; the service reads it. Neither project references the other,
/// so this is the only place the two shapes are held together.
/// </summary>
public sealed class ScheduleFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fortiq-schedule-").FullName;

    [Fact]
    public async Task TheServiceCanReadTheScheduleTheWizardWrote()
    {
        var request = new ProtectRepositoryRequest(
            Path.Combine(_directory, "repo"),
            Path.Combine(_directory, "kit"),
            Path.Combine(_directory, "documents"));

        await ProtectRepositoryAdapter.WriteScheduleAsync(
            Path.Combine(_directory, "schedules"),
            "AABBCCDD",
            request,
            new TimeOnly(2, 30),
            CancellationToken.None);

        var schedules = await new FileSystemScheduleStore(_directory).ReadSchedulesAsync(CancellationToken.None);

        var schedule = Assert.Single(schedules);
        Assert.Equal("AABBCCDD", schedule.Id);
        Assert.Equal(Path.GetFullPath(request.SourcePath), schedule.SourcePath);
        Assert.True(schedule.Enabled);
        Assert.Equal(new TimeOnly(2, 30), Assert.IsType<DailyAt>(schedule.Recurrence).TimeOfDay);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
