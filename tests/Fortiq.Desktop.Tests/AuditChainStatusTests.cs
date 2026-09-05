using Fortiq.Desktop;
using Fortiq.Infrastructure.Receipts;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// What the audit card is allowed to claim, given what verification actually found.
/// </summary>
/// <remarks>
/// The card used to classify itself by searching its own sentence for "anomaly", "tampering" or
/// "failed". The wording it produced on success - "0 tampering detected" - contains one of those
/// words, so the green case depended on a substring search not noticing a word the same code had
/// just written.
/// </remarks>
public sealed class AuditChainStatusTests
{
    private static AuditRepositoryLedgerReport Report(LedgerTrust trust, int receipts = 3) =>
        new("repo", trust != LedgerTrust.Broken, receipts, 1, receipts, "genesis", "head",
            trust == LedgerTrust.Broken ? [Anomaly()] : [],
            trust == LedgerTrust.LegacyUnverified ? receipts : 0,
            trust);

    private static AuditLedgerAnomaly Anomaly() =>
        new("repo", 2, "ReceiptHashMismatch", "Receipt 2 hash verification failed.");

    [Fact]
    public void AnUnbrokenChainIsVerified()
    {
        var status = AuditChainStatus.From(
            new AuditLedgerVerificationResult(true, 3, [Report(LedgerTrust.Verified)], []));

        Assert.Equal(AuditChainState.Verified, status.State);
    }

    [Fact]
    public void ReceiptsThatPredateTheChainAreNotReportedAsVerified()
    {
        // IsValid is true here: unchained receipts carry no hash, so there was nothing to find fault
        // with. Painting that green is the one claim this card exists not to make.
        var status = AuditChainStatus.From(
            new AuditLedgerVerificationResult(true, 3, [Report(LedgerTrust.LegacyUnverified)], []));

        Assert.Equal(AuditChainState.LegacyUnverified, status.State);
        Assert.NotEqual(AuditChainStatus.From(
            new AuditLedgerVerificationResult(true, 3, [Report(LedgerTrust.Verified)], [])).Badge, status.Badge);
    }

    [Fact]
    public void OneUnverifiableLedgerHoldsBackTheWholeVerdict()
    {
        var status = AuditChainStatus.From(new AuditLedgerVerificationResult(
            true, 6, [Report(LedgerTrust.Verified), Report(LedgerTrust.LegacyUnverified)], []));

        Assert.Equal(AuditChainState.LegacyUnverified, status.State);
    }

    [Fact]
    public void ABrokenChainIsAnAnomalyAndSaysWhat()
    {
        var status = AuditChainStatus.From(new AuditLedgerVerificationResult(
            false, 3, [Report(LedgerTrust.Broken)], [Anomaly()]));

        Assert.Equal(AuditChainState.Anomaly, status.State);
        Assert.Contains("hash verification failed", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCheckIsNotAFailedChain()
    {
        var status = AuditChainStatus.Failed("the receipts folder could not be read.");

        Assert.Equal(AuditChainState.Error, status.State);
        Assert.DoesNotContain("broken", status.Badge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoLedgersAtAllIsNotAVerifiedResult()
    {
        var status = AuditChainStatus.From(new AuditLedgerVerificationResult(true, 0, [], []));

        Assert.Equal(AuditChainState.NothingRecorded, status.State);
    }
}
