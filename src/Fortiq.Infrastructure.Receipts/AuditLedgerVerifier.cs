using System.Text.Json;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Receipts;

/// <summary>
/// Specific anomaly or security violation detected during ledger verification.
/// </summary>
public sealed record AuditLedgerAnomaly(
    string RepositoryId,
    long SequenceNumber,
    string AnomalyType,
    string Description,
    Guid? OperationId = null);

/// <summary>How far a repository's receipts could actually be checked.</summary>
public enum LedgerTrust
{
    /// <summary>A chain was verified end to end.</summary>
    Verified,

    /// <summary>
    /// Only receipts predating the chained schema were found. Nothing here is wrong; nothing here can
    /// be checked either, and the two must not be reported as the same answer.
    /// </summary>
    LegacyUnverified,

    /// <summary>The chain is present and does not hold.</summary>
    Broken
}

/// <summary>
/// Verification report for a single repository's cryptographic receipt ledger.
/// </summary>
public sealed record AuditRepositoryLedgerReport(
    string RepositoryId,
    bool IsValid,
    int TotalReceipts,
    long FirstSequenceNumber,
    long LastSequenceNumber,
    string? GenesisHash,
    string? HeadHash,
    IReadOnlyList<AuditLedgerAnomaly> Anomalies,
    int LegacyReceiptCount = 0,
    /// <summary>
    /// What <see cref="IsValid"/> is worth. <c>IsValid</c> means no anomaly was found, which for a
    /// repository holding only version 1 receipts is true and says nothing: those receipts carry no
    /// hash, so there was no chain to find fault with. Reporting that as verified would let an
    /// unverifiable history read exactly like a proven one.
    /// </summary>
    LedgerTrust Trust = LedgerTrust.Verified);

/// <summary>
/// Comprehensive verification result for all repositories audited in a receipt directory.
/// </summary>
public sealed record AuditLedgerVerificationResult(
    bool IsValid,
    int TotalReceiptsVerified,
    IReadOnlyList<AuditRepositoryLedgerReport> Repositories,
    IReadOnlyList<AuditLedgerAnomaly> AllAnomalies);

/// <summary>
/// Mathematically verifies the cryptographic integrity and unbroken continuity of operation receipts
/// according to ADR-007, Spec 15, and DEC-013. Detects receipt tampering, sequence gaps,
/// deletion, splicing, duplicate sequences, and ledger truncation.
/// </summary>
public static class AuditLedgerVerifier
{
    public static async Task<AuditLedgerVerificationResult> VerifyLedgerAsync(
        string receiptsDirectory,
        string? repositoryId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(receiptsDirectory) || !Directory.Exists(receiptsDirectory))
        {
            return new AuditLedgerVerificationResult(true, 0, Array.Empty<AuditRepositoryLedgerReport>(), Array.Empty<AuditLedgerAnomaly>());
        }

        var unparsedAnomalies = new List<AuditLedgerAnomaly>();
        var parsedReceipts = new List<OperationReceipt>();

        foreach (var file in Directory.GetFiles(receiptsDirectory, "*.json"))
        {
            if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var receipt = await FileSystemOperationReceiptStore.LoadReceiptAsync(file, cancellationToken);
                if (receipt is not null)
                {
                    parsedReceipts.Add(receipt);
                }
            }
            catch (Exception ex)
            {
                unparsedAnomalies.Add(new AuditLedgerAnomaly(
                    repositoryId ?? "unknown",
                    0,
                    "MalformedReceiptJson",
                    $"Failed to parse receipt file '{Path.GetFileName(file)}': {ex.Message}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            parsedReceipts = parsedReceipts
                .Where(r => string.Equals(r.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var repoReports = new List<AuditRepositoryLedgerReport>();
        var repoGroups = parsedReceipts.GroupBy(r => r.RepositoryId, StringComparer.OrdinalIgnoreCase);

        foreach (var group in repoGroups)
        {
            var repoKey = group.Key;
            var legacyReceipts = group.Where(r => r.Version < 2 && r.SequenceNumber == 0).ToList();
            var chainedReceipts = group.Where(r => r.Version >= 2 || r.SequenceNumber > 0)
                .OrderBy(r => r.SequenceNumber).ThenBy(r => r.StartedAt).ToList();
            var repoAnomalies = new List<AuditLedgerAnomaly>();

            if (chainedReceipts.Count > 0)
            {
                // 1. Verify Genesis start
                var first = chainedReceipts[0];
                if (first.SequenceNumber != 1)
                {
                    repoAnomalies.Add(new AuditLedgerAnomaly(
                        repoKey,
                        first.SequenceNumber,
                        "SequenceStartMismatch",
                        $"First chained receipt has sequence {first.SequenceNumber} (expected 1). Leading receipts may have been deleted.",
                        first.OperationId));
                }

                if (!string.Equals(first.PreviousReceiptHash, OperationReceipt.GenesisHash, StringComparison.OrdinalIgnoreCase))
                {
                    repoAnomalies.Add(new AuditLedgerAnomaly(
                        repoKey,
                        first.SequenceNumber,
                        "GenesisHashMismatch",
                        $"Genesis receipt previousReceiptHash is '{first.PreviousReceiptHash}' (expected '{OperationReceipt.GenesisHash}').",
                        first.OperationId));
                }

                // 2. Iterate and verify continuity, tamper resistance, and chaining
                for (var i = 0; i < chainedReceipts.Count; i++)
                {
                    var current = chainedReceipts[i];
                    var expectedSeq = (long)(i + 1);

                    // Check monotonic numbering
                    if (current.SequenceNumber != expectedSeq)
                    {
                        if (i > 0 && current.SequenceNumber == chainedReceipts[i - 1].SequenceNumber)
                        {
                            repoAnomalies.Add(new AuditLedgerAnomaly(
                                repoKey,
                                current.SequenceNumber,
                                "DuplicateSequenceNumber",
                                $"Duplicate sequence number {current.SequenceNumber} detected on receipt {current.OperationId}.",
                                current.OperationId));
                        }
                        else
                        {
                            repoAnomalies.Add(new AuditLedgerAnomaly(
                                repoKey,
                                current.SequenceNumber,
                                "SequenceGap",
                                $"Sequence gap detected: expected {expectedSeq}, but found {current.SequenceNumber}. Deletion of receipts detected.",
                                current.OperationId));
                        }
                    }

                    // Check cryptographic content hash
                    var computedHash = OperationReceipt.ComputeCanonicalHash(
                        current.OperationId,
                        current.Operation,
                        current.RepositoryId,
                        current.Engine,
                        current.StartedAt,
                        current.CompletedAt,
                        current.EngineResult,
                        current.SnapshotId,
                        current.Source,
                        current.Metrics,
                        current.Warnings,
                        current.SequenceNumber,
                        current.PreviousReceiptHash ?? OperationReceipt.GenesisHash,
                        current.Version);

                    if (!string.Equals(computedHash, current.ReceiptHash, StringComparison.OrdinalIgnoreCase))
                    {
                        repoAnomalies.Add(new AuditLedgerAnomaly(
                            repoKey,
                            current.SequenceNumber,
                            "TamperedReceipt",
                            $"Receipt {current.OperationId} (seq {current.SequenceNumber}) hash verification failed: recorded '{current.ReceiptHash}', computed '{computedHash}'. Content has been altered.",
                            current.OperationId));
                    }

                    // Check previous hash linkage
                    if (i > 0)
                    {
                        var previous = chainedReceipts[i - 1];
                        if (!string.Equals(current.PreviousReceiptHash, previous.ReceiptHash, StringComparison.OrdinalIgnoreCase))
                        {
                            repoAnomalies.Add(new AuditLedgerAnomaly(
                                repoKey,
                                current.SequenceNumber,
                                "BrokenHashChain",
                                $"Hash chain broken at sequence {current.SequenceNumber}: points to '{current.PreviousReceiptHash}', expected '{previous.ReceiptHash}'. Splicing or reordering detected.",
                                current.OperationId));
                        }
                    }

                    // Check chronological sanity
                    if (current.CompletedAt < current.StartedAt)
                    {
                        repoAnomalies.Add(new AuditLedgerAnomaly(
                            repoKey,
                            current.SequenceNumber,
                            "InvalidTimestamp",
                            $"Receipt {current.OperationId} completedAt ({current.CompletedAt:O}) is earlier than startedAt ({current.StartedAt:O}).",
                            current.OperationId));
                    }
                }

                // 3. Verify against repository .ledger state file
                var ledgerState = await FileSystemOperationReceiptStore.LoadLedgerStateAsync(receiptsDirectory, repoKey, cancellationToken);
                if (ledgerState is not null)
                {
                    var maxPresentSequence = chainedReceipts.Max(r => r.SequenceNumber);
                    if (ledgerState.SequenceNumber > maxPresentSequence)
                    {
                        repoAnomalies.Add(new AuditLedgerAnomaly(
                            repoKey,
                            maxPresentSequence,
                            "ChainTruncated",
                            $"Ledger state tracks sequence {ledgerState.SequenceNumber} (hash '{ledgerState.LastReceiptHash}'), but latest receipt found is sequence {maxPresentSequence}. Trailing receipts have been truncated or deleted."));
                    }
                    else if (ledgerState.SequenceNumber == maxPresentSequence &&
                             !string.Equals(ledgerState.LastReceiptHash, chainedReceipts.Last().ReceiptHash, StringComparison.OrdinalIgnoreCase))
                    {
                        repoAnomalies.Add(new AuditLedgerAnomaly(
                            repoKey,
                            maxPresentSequence,
                            "LedgerHeadMismatch",
                            $"Ledger state head hash '{ledgerState.LastReceiptHash}' does not match last receipt hash '{chainedReceipts.Last().ReceiptHash}'."));
                    }
                }

                repoReports.Add(new AuditRepositoryLedgerReport(
                    repoKey,
                    repoAnomalies.Count == 0,
                    chainedReceipts.Count + legacyReceipts.Count,
                    first.SequenceNumber,
                    chainedReceipts.Last().SequenceNumber,
                    first.PreviousReceiptHash,
                    chainedReceipts.Last().ReceiptHash,
                    repoAnomalies,
                    legacyReceipts.Count,
                    repoAnomalies.Count == 0 ? LedgerTrust.Verified : LedgerTrust.Broken));
            }
            else if (legacyReceipts.Count > 0)
            {
                repoReports.Add(new AuditRepositoryLedgerReport(
                    repoKey,
                    true,
                    legacyReceipts.Count,
                    0,
                    0,
                    null,
                    null,
                    repoAnomalies,
                    legacyReceipts.Count,
                    LedgerTrust.LegacyUnverified));
            }
        }

        var allAnomalies = unparsedAnomalies.Concat(repoReports.SelectMany(r => r.Anomalies)).ToList();
        var isValid = allAnomalies.Count == 0;
        var totalReceipts = parsedReceipts.Count;

        return new AuditLedgerVerificationResult(isValid, totalReceipts, repoReports, allAnomalies);
    }
}
