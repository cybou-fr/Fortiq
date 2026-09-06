using Fortiq.Monitoring;

namespace Fortiq.Monitoring.Tests;

/// <summary>
/// What the health verdict says about a repository whose recovery phrase nobody confirmed.
/// </summary>
/// <remarks>
/// This is the case the whole product is built to prevent and could not previously see: backups
/// running nightly, drills passing, storage immutable, every check green - and 24 words that exist
/// nowhere, so the owner cannot open any of it on another machine. It has to outrank the good news.
/// </remarks>
public sealed class RecoveryPhraseHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APhraseNobodyConfirmedPutsAnOtherwisePerfectRepositoryAtRisk()
    {
        var health = HealthAssessor.Assess(Facts(RecoveryPhraseState.Issued), Now);

        Assert.Equal(HealthVerdict.AtRisk, health.Verdict);
        var finding = Assert.Single(health.Findings, item => item.Code == "recovery-phrase-unconfirmed");
        Assert.Contains("another machine", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfirmedPhraseSaysNothingAtAll()
    {
        var health = HealthAssessor.Assess(Facts(RecoveryPhraseState.Confirmed), Now);

        Assert.Equal(HealthVerdict.Recoverable, health.Verdict);
        Assert.Empty(health.Findings);
    }

    [Fact]
    public void ARepositoryFromBeforeThisRecordExistedIsNotAccused()
    {
        // Unknown is what every installation predating the record reports, and most of those owners
        // did write their words down. A product that cries wolf teaches people to ignore it.
        var health = HealthAssessor.Assess(Facts(RecoveryPhraseState.Unknown), Now);

        Assert.Equal(HealthVerdict.Recoverable, health.Verdict);
        Assert.DoesNotContain(health.Findings, item => item.Code == "recovery-phrase-unconfirmed");
    }

    [Fact]
    public void TheDefaultIsSilence()
    {
        // Every caller that predates this fact - and there are many - keeps reporting what it did.
        var facts = new RepositoryFacts(
            "a",
            "documents",
            LastBackupAt: Now.AddHours(-1),
            LastHealthyCheckAt: Now.AddDays(-1),
            LastProvenRestoreAt: Now.AddDays(-2),
            KitPresent: true,
            StorageImmutable: true,
            StorageProtectionNow: StorageProtectionStatus.Immutable);

        Assert.Equal(RecoveryPhraseState.Unknown, facts.RecoveryPhrase);
        Assert.Equal(HealthVerdict.Recoverable, HealthAssessor.Assess(facts, Now).Verdict);
    }

    private static RepositoryFacts Facts(RecoveryPhraseState phrase) => new(
        "a",
        "documents",
        LastBackupAt: Now.AddHours(-1),
        LastHealthyCheckAt: Now.AddDays(-1),
        LastProvenRestoreAt: Now.AddDays(-2),
        KitPresent: true,
        StorageImmutable: true,
        StorageProtectionNow: StorageProtectionStatus.Immutable,
        RecoveryPhrase: phrase);
}
