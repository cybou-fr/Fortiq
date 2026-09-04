using System.Text.Json;
using Fortiq.Monitoring;

namespace Fortiq.Monitoring.Tests;

/// <summary>
/// What Fortiq is willing to claim about a repository. The claim that matters is not "the job ran"
/// but "the data comes back", and the difference is what these tests hold to.
/// </summary>
public sealed class HealthTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-health-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARepositoryBackedUpCheckedAndRestoredRecentlyIsRecoverable()
    {
        var health = HealthAssessor.Assess(Facts(), Now);

        Assert.Equal(HealthVerdict.Recoverable, health.Verdict);
        Assert.Empty(health.Findings);
    }

    [Fact]
    public void ABackupNobodyHasEverRestoredIsNotCalledHealthy()
    {
        var health = HealthAssessor.Assess(Facts() with { LastProvenRestoreAt = null }, Now);

        // This is the whole point of the product: a backup that has never been restored is a belief
        // about recoverability, not evidence of it.
        Assert.Equal(HealthVerdict.Unproven, health.Verdict);
        Assert.Contains(health.Findings, finding => finding.Code == "restore-never-proven");
    }

    [Fact]
    public void AStaleRestoreProofIsUnprovenRatherThanFine()
    {
        var health = HealthAssessor.Assess(Facts() with { LastProvenRestoreAt = Now.AddDays(-90) }, Now);

        Assert.Equal(HealthVerdict.Unproven, health.Verdict);
        Assert.Contains(health.Findings, finding => finding.Code == "restore-proof-stale");
    }

    [Fact]
    public void AMissingKitIsARiskEvenWhenEverythingElseLooksGood()
    {
        var health = HealthAssessor.Assess(Facts() with { KitPresent = false }, Now);

        // Without a kit the repository cannot be opened anywhere else, however healthy it looks here.
        Assert.Equal(HealthVerdict.AtRisk, health.Verdict);
        Assert.Contains(health.Findings, finding => finding.Code == "kit-missing");
    }

    [Theory]
    [InlineData("never-backed-up")]
    [InlineData("backup-stale")]
    [InlineData("last-run-failed")]
    public void ThingsThatWouldHurtTodayAreARisk(string code)
    {
        var facts = code switch
        {
            "never-backed-up" => Facts() with { LastBackupAt = null },
            "backup-stale" => Facts() with { LastBackupAt = Now.AddDays(-5) },
            _ => Facts() with { LastFailure = "the repository was unreachable" }
        };

        var health = HealthAssessor.Assess(facts, Now);

        Assert.Equal(HealthVerdict.AtRisk, health.Verdict);
        Assert.Contains(health.Findings, finding => finding.Code == code);
    }

    [Fact]
    public void StorageThatKeepsNothingIsReportedWithoutBeingCalledARiskToday()
    {
        var health = HealthAssessor.Assess(Facts() with { StorageImmutable = false }, Now);

        // It is a weakness rather than an outage: the data is there today, and nothing protects it
        // from whoever holds the credentials.
        Assert.Equal(HealthVerdict.Unproven, health.Verdict);
        Assert.Contains(health.Findings, finding => finding.Code == "storage-not-immutable");
    }

    [Fact]
    public void TheReportTakesTheWorstVerdictItHolds()
    {
        var report = new HealthReport(Now, [
            HealthAssessor.Assess(Facts(), Now),
            HealthAssessor.Assess(Facts() with { RepositoryId = "b", KitPresent = false }, Now)
        ]);

        Assert.Equal(HealthVerdict.AtRisk, report.Worst);
    }

    [Fact]
    public async Task EvidenceComesFromReceiptsAndOnlySuccessesCount()
    {
        await WriteReceiptAsync("backup", "succeeded", Now.AddHours(-2));
        await WriteReceiptAsync("check", "failed", Now.AddHours(-1), "the repository contains errors");
        await WriteReceiptAsync("restore", "succeeded", Now.AddDays(-3));

        var evidence = Assert.Single(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None));

        Assert.Equal(Now.AddHours(-2), evidence.LastBackupAt);
        Assert.Equal(Now.AddDays(-3), evidence.LastProvenRestoreAt);

        // A check that failed does not make a repository checked, and the failure is the most recent
        // thing that happened, so it is reported.
        Assert.Null(evidence.LastHealthyCheckAt);
        Assert.Equal("the repository contains errors", evidence.LastFailure);
    }

    [Fact]
    public async Task AFailureOlderThanTheLastSuccessIsHistoryRatherThanAProblem()
    {
        await WriteReceiptAsync("backup", "failed", Now.AddDays(-2), "the disk was full");
        await WriteReceiptAsync("backup", "succeeded", Now.AddHours(-1));

        var evidence = Assert.Single(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None));

        Assert.Null(evidence.LastFailure);
        Assert.Equal(Now.AddHours(-1), evidence.LastBackupAt);
    }

    [Fact]
    public async Task ADamagedReceiptDoesNotStopMonitoring()
    {
        await WriteReceiptAsync("backup", "succeeded", Now.AddHours(-1));
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.json"), "{ this is not json");

        var evidence = Assert.Single(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None));

        Assert.Equal(Now.AddHours(-1), evidence.LastBackupAt);
    }

    [Fact]
    public async Task TheReportIsWrittenWhereSomethingElseCanReadIt()
    {
        var report = new HealthReport(Now, [HealthAssessor.Assess(Facts(), Now)]);
        var json = Path.Combine(_directory, "health.json");

        await HealthPublication.WriteJsonAsync(report, json, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(json));
        Assert.Equal("fortiq.health-report", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal("recoverable", document.RootElement.GetProperty("worst").GetString());
        Assert.Single(document.RootElement.GetProperty("repositories").EnumerateArray());
    }

    [Fact]
    public void MetricsSayHowStaleEachKindOfEvidenceIs()
    {
        var report = new HealthReport(Now, [
            HealthAssessor.Assess(Facts() with { LastProvenRestoreAt = null }, Now)
        ]);

        var text = HealthPublication.ToPrometheusText(report);

        Assert.Contains("fortiq_repository_recoverable{repository=\"a\",schedule=\"documents\"} 0", text, StringComparison.Ordinal);
        Assert.Contains("fortiq_repository_last_backup_age_seconds{repository=\"a\",schedule=\"documents\"} 3600", text, StringComparison.Ordinal);

        // A restore that never happened is absent rather than reported as zero seconds ago, which
        // would read as "just now".
        Assert.DoesNotContain("fortiq_repository_last_restore_proof_age_seconds{repository=\"a\"", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RepositoryFacts Facts() => new(
        "a",
        "documents",
        LastBackupAt: Now.AddHours(-1),
        LastHealthyCheckAt: Now.AddDays(-2),
        LastProvenRestoreAt: Now.AddDays(-7),
        KitPresent: true,
        StorageImmutable: true);

    private async Task WriteReceiptAsync(string operation, string result, DateTimeOffset completedAt, string? warning = null)
    {
        Directory.CreateDirectory(_directory);
        var receipt = new
        {
            schema = "fortiq.operation-receipt",
            version = 1,
            operationId = Guid.NewGuid(),
            operation,
            repositoryId = "A",
            engine = new { name = "restic", version = "0.19.1", sha256 = new string('a', 64) },
            startedAt = completedAt.AddSeconds(-30),
            completedAt,
            engineResult = result,
            metrics = new Dictionary<string, long>(),
            warnings = warning is null ? Array.Empty<string>() : [warning]
        };

        await File.WriteAllTextAsync(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.json"),
            JsonSerializer.Serialize(receipt));
    }
}
