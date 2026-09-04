using Fortiq.Monitoring;

namespace Fortiq.Monitoring.Tests;

/// <summary>
/// What the storage promises now, as against what it promised when the repository was created.
/// </summary>
/// <remarks>
/// Health used to report the protection recorded in the kit at provisioning time, which a dashboard
/// reads as a statement about today. A bucket whose retention had been lifted last week still showed
/// as immutable — and lifting that retention is the first move of somebody preparing to delete the
/// backups, so the one moment the report most needed to change was the moment it could not.
/// </remarks>
public sealed class StorageProtectionCurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static RepositoryFacts Facts(bool immutableAtProvisioning, StorageProtectionStatus now) => new(
        "a",
        "documents",
        LastBackupAt: Now.AddHours(-1),
        LastHealthyCheckAt: Now.AddDays(-1),
        LastProvenRestoreAt: Now.AddDays(-2),
        KitPresent: true,
        StorageImmutable: immutableAtProvisioning,
        LastFailure: null,
        StorageProtectionNow: now);

    [Fact]
    public void ProtectionRemovedSinceProvisioningIsCalledOutForWhatItIs()
    {
        var health = HealthAssessor.Assess(
            Facts(immutableAtProvisioning: true, StorageProtectionStatus.NotImmutable),
            Now);

        var finding = Assert.Single(health.Findings);
        Assert.Equal("storage-protection-lost", finding.Code);

        // Losing retention is not the same as never having had it, and the report must not flatten
        // the two: one is a configuration someone chose, the other is a change somebody made.
        Assert.Contains("no longer", finding.Detail, StringComparison.Ordinal);
        Assert.NotEqual(HealthVerdict.Recoverable, health.Verdict);
    }

    [Fact]
    public void StorageThatStillProtectsRaisesNothing()
    {
        var health = HealthAssessor.Assess(
            Facts(immutableAtProvisioning: true, StorageProtectionStatus.Immutable),
            Now);

        Assert.Empty(health.Findings);
        Assert.Equal(HealthVerdict.Recoverable, health.Verdict);
    }

    [Fact]
    public void StorageThatCouldNotBeAskedIsNotReportedAsProtected()
    {
        var health = HealthAssessor.Assess(
            Facts(immutableAtProvisioning: true, StorageProtectionStatus.Unknown),
            Now);

        // The dangerous reading is the reassuring one. If "could not ask" resolved to "protected",
        // the report would go quiet exactly when somebody had taken the protection away.
        var finding = Assert.Single(health.Findings);
        Assert.Equal("storage-protection-unknown", finding.Code);
        Assert.Contains("unverified", finding.Detail, StringComparison.Ordinal);
        Assert.NotEqual(HealthVerdict.Recoverable, health.Verdict);
    }

    [Fact]
    public void APlainDirectoryStillReportsWhatItAlwaysDid()
    {
        // Nothing changed for the ordinary case: storage that never promised anything says so, and
        // does not acquire a new and more alarming finding because this distinction now exists.
        foreach (var status in new[] { StorageProtectionStatus.NotImmutable, StorageProtectionStatus.Unknown })
        {
            var health = HealthAssessor.Assess(Facts(immutableAtProvisioning: false, status), Now);
            Assert.Equal("storage-not-immutable", Assert.Single(health.Findings).Code);
        }
    }

    [Fact]
    public void LosingProtectionDoesNotClaimTheRepositoryIsUnrecoverableToday()
    {
        var health = HealthAssessor.Assess(
            Facts(immutableAtProvisioning: true, StorageProtectionStatus.NotImmutable),
            Now);

        // The snapshots are all still there. What has gone is the guarantee that they will stay, and
        // saying "may not be recoverable today" about data that restores fine would be false — and
        // would teach whoever reads it that the worst verdict does not mean much.
        Assert.Equal(HealthVerdict.Unproven, health.Verdict);
    }
}
