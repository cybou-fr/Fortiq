using Fortiq.Infrastructure.Receipts;

namespace Fortiq.Desktop;

/// <summary>What the audit card knows about the receipt chain right now.</summary>
public enum AuditChainState
{
    /// <summary>Nothing is protected yet, so there is no history to check.</summary>
    NothingRecorded,

    /// <summary>Receipts exist; nobody has asked for them to be verified in this session.</summary>
    NotChecked,

    /// <summary>A chain was read end to end and holds.</summary>
    Verified,

    /// <summary>
    /// Only receipts predating the chained schema were found. Nothing is wrong and nothing can be
    /// proven, which is a third answer rather than a shade of the other two.
    /// </summary>
    LegacyUnverified,

    /// <summary>The chain is present and does not hold: something altered, removed or spliced it.</summary>
    Anomaly,

    /// <summary>Verification could not run. This says nothing about the chain itself.</summary>
    Error
}

/// <summary>
/// The audit card's state and the sentence under it.
/// </summary>
/// <remarks>
/// The card used to decide what it was showing by searching its own status sentence for "anomaly",
/// "tampering" or "failed". Two things were wrong with that. Any wording change silently reclassified
/// the result - a verified chain reported as "0 tampering detected" contains the word "tampering" and
/// read as an anomaly. And the verifier's own <see cref="LedgerTrust"/> was thrown away on the way to
/// the screen, so a repository holding only unchained legacy receipts came back
/// <c>IsValid = true</c> and was painted green: an unverifiable history displayed exactly like a
/// proven one, which is the single claim this card exists not to make.
/// </remarks>
public sealed record AuditChainStatus(AuditChainState State, string Detail)
{
    /// <summary>Before anything has been protected.</summary>
    public static AuditChainStatus NothingRecorded { get; } = new(
        AuditChainState.NothingRecorded,
        "Receipts are written as backups run. There is nothing recorded yet.");

    /// <summary>Receipts exist but have not been verified in this session.</summary>
    public static AuditChainStatus NotChecked { get; } = new(
        AuditChainState.NotChecked,
        "Every operation is recorded in a receipt that carries the hash of the one before it. " +
        "Verifying re-reads the chain on disk and checks that no receipt was altered, removed or reordered.");

    /// <summary>Reads a completed verification run.</summary>
    public static AuditChainStatus From(AuditLedgerVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.AllAnomalies.Count > 0)
        {
            return new(
                AuditChainState.Anomaly,
                result.AllAnomalies.Count == 1
                    ? result.AllAnomalies[0].Description
                    : $"{result.AllAnomalies.Count} problems found. First: {result.AllAnomalies[0].Description}");
        }

        if (result.Repositories.Count == 0)
        {
            return NothingRecorded;
        }

        var legacyOnly = result.Repositories.Count(report => report.Trust == LedgerTrust.LegacyUnverified);
        var verified = result.Repositories.Count(report => report.Trust == LedgerTrust.Verified);

        if (legacyOnly > 0 && verified == 0)
        {
            return new(
                AuditChainState.LegacyUnverified,
                $"{result.TotalReceiptsVerified} receipt(s) found, all written before receipts carried hashes. " +
                "Nothing here is wrong and nothing here can be proven. Receipts written from now on are chained.");
        }

        if (legacyOnly > 0)
        {
            return new(
                AuditChainState.LegacyUnverified,
                $"{verified} repository ledger(s) verified end to end. {legacyOnly} hold only receipts written " +
                "before receipts carried hashes, which cannot be checked either way.");
        }

        return new(
            AuditChainState.Verified,
            $"{result.TotalReceiptsVerified} receipt(s) across {result.Repositories.Count} repository ledger(s). " +
            "The chain is unbroken: no gaps, no reordering, no altered content.");
    }

    /// <summary>Verification itself failed. The chain is neither proven nor disproven.</summary>
    public static AuditChainStatus Failed(string reason) => new(
        AuditChainState.Error,
        $"The check could not run: {reason} This says nothing about the receipts themselves - try again.");

    /// <summary>The words on the badge.</summary>
    public string Badge => State switch
    {
        AuditChainState.NothingRecorded => "Audit chain: nothing recorded yet",
        AuditChainState.NotChecked => "Audit chain: not checked yet",
        AuditChainState.Verified => "Audit chain: verified",
        AuditChainState.LegacyUnverified => "Audit chain: cannot be verified",
        AuditChainState.Anomaly => "Audit chain: broken",
        AuditChainState.Error => "Audit chain: check did not run",
        _ => "Audit chain: unknown"
    };
}
