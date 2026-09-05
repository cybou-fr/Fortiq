using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;

namespace Fortiq.Security.Tests;

/// <summary>
/// Verifies the cryptographic integrity, tamper detection, sequence gap detection,
/// and hash-chaining properties of the Fortiq Audit Ledger according to ADR-007, Spec 15, and DEC-013.
/// </summary>
public sealed class AuditLedgerChainingTests : IDisposable
{
    private static readonly EngineIdentity Engine = new("restic", "0.19.1", new string('a', 64));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-audit-test-" + Guid.NewGuid().ToString("N"));

    public AuditLedgerChainingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private static OperationReceipt CreateReceipt(
        string repositoryId = "repo-alpha",
        OperationKind operation = OperationKind.Backup,
        EngineResult result = EngineResult.Succeeded,
        long files = 10,
        long bytes = 1024)
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationReceipt(
            OperationId: Guid.NewGuid(),
            Operation: operation,
            RepositoryId: repositoryId,
            Engine: Engine,
            StartedAt: now.AddSeconds(-2),
            CompletedAt: now,
            EngineResult: result,
            SnapshotId: Guid.NewGuid().ToString("N"),
            Source: new ReceiptSource("directory", "c:\\data"),
            Metrics: new Dictionary<string, long>(StringComparer.Ordinal) { ["files"] = files, ["bytes"] = bytes },
            Warnings: Array.Empty<string>());
    }

    [Fact]
    public async Task ValidReceiptSequenceFormsUnbrokenCryptographicChain()
    {
        var store = new FileSystemOperationReceiptStore(_directory);

        // Save 5 sequential receipts
        var kinds = new[] { OperationKind.Initialize, OperationKind.Backup, OperationKind.Snapshots, OperationKind.Check, OperationKind.RestoreProof };
        foreach (var kind in kinds)
        {
            await store.SaveAsync(CreateReceipt("repo-1", kind), CancellationToken.None);
        }

        var receipts = await store.LoadAllReceiptsAsync("repo-1");
        Assert.Equal(5, receipts.Count);

        // Verify sequence numbers and cryptographic chaining
        for (int i = 0; i < receipts.Count; i++)
        {
            var current = receipts[i];
            Assert.Equal(i + 1, current.SequenceNumber);
            Assert.NotNull(current.ReceiptHash);

            if (i == 0)
            {
                Assert.Equal(OperationReceipt.GenesisHash, current.PreviousReceiptHash);
            }
            else
            {
                var prev = receipts[i - 1];
                Assert.Equal(prev.ReceiptHash, current.PreviousReceiptHash);
            }
        }

        // Verify with AuditLedgerVerifier
        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.True(report.IsValid);
        Assert.Equal(5, report.TotalReceiptsVerified);
        Assert.Empty(report.AllAnomalies);

        var repoReport = Assert.Single(report.Repositories);
        Assert.Equal("repo-1", repoReport.RepositoryId);
        Assert.True(repoReport.IsValid);
        Assert.Equal(1, repoReport.FirstSequenceNumber);
        Assert.Equal(5, repoReport.LastSequenceNumber);
        Assert.Equal(receipts[4].ReceiptHash, repoReport.HeadHash);
    }

    [Fact]
    public async Task TamperingWithReceiptContentIsDetectedByAuditLedgerVerifier()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        for (int i = 0; i < 3; i++)
        {
            await store.SaveAsync(CreateReceipt("repo-tamper"), CancellationToken.None);
        }

        var receipts = await store.LoadAllReceiptsAsync("repo-tamper");
        var victim = receipts[1]; // Sequence 2
        var filePath = Path.Combine(_directory, $"{victim.OperationId:D}.json");

        // Tamper with receipt file content on disk (change metric bytes from 1024 to 999999)
        var jsonText = await File.ReadAllTextAsync(filePath);
        var jsonNode = JsonNode.Parse(jsonText)!;
        jsonNode["metrics"]!["bytes"] = 999999;
        await File.WriteAllTextAsync(filePath, jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Audit verification must detect tampering
        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.False(report.IsValid);
        Assert.Contains(report.AllAnomalies, a =>
            a.AnomalyType == "TamperedReceipt" &&
            a.SequenceNumber == 2 &&
            a.OperationId == victim.OperationId);
    }

    [Fact]
    public async Task DeletingReceiptFromMiddleOfChainIsDetectedAsSequenceGapAndBrokenChain()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        for (int i = 0; i < 4; i++)
        {
            await store.SaveAsync(CreateReceipt("repo-del"), CancellationToken.None);
        }

        var receipts = await store.LoadAllReceiptsAsync("repo-del");
        var victim = receipts[2]; // Sequence 3
        var filePath = Path.Combine(_directory, $"{victim.OperationId:D}.json");
        File.Delete(filePath);

        // Audit verification must detect both sequence gap and broken hash chain
        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.False(report.IsValid);
        Assert.Contains(report.AllAnomalies, a => a.AnomalyType == "SequenceGap");
        Assert.Contains(report.AllAnomalies, a => a.AnomalyType == "BrokenHashChain");
    }

    [Fact]
    public async Task DeletingGenesisReceiptIsDetectedAsSequenceStartMismatch()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        for (int i = 0; i < 3; i++)
        {
            await store.SaveAsync(CreateReceipt("repo-genesis"), CancellationToken.None);
        }

        var receipts = await store.LoadAllReceiptsAsync("repo-genesis");
        var genesis = receipts[0]; // Sequence 1
        var filePath = Path.Combine(_directory, $"{genesis.OperationId:D}.json");
        File.Delete(filePath);

        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.False(report.IsValid);
        Assert.Contains(report.AllAnomalies, a => a.AnomalyType == "SequenceStartMismatch");
    }

    [Fact]
    public async Task TruncatingTrailingReceiptsIsDetectedViaLedgerState()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        for (int i = 0; i < 4; i++)
        {
            await store.SaveAsync(CreateReceipt("repo-trunc"), CancellationToken.None);
        }

        var receipts = await store.LoadAllReceiptsAsync("repo-trunc");
        var tail = receipts[3]; // Sequence 4
        var filePath = Path.Combine(_directory, $"{tail.OperationId:D}.json");
        File.Delete(filePath);

        // Ledger state still expects sequence 4
        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.False(report.IsValid);
        Assert.Contains(report.AllAnomalies, a => a.AnomalyType == "ChainTruncated" && a.SequenceNumber == 3);
    }

    [Fact]
    public async Task MultiRepositoryReceiptsMaintainIndependentChains()
    {
        var store = new FileSystemOperationReceiptStore(_directory);

        // Interleave saves across repo A and repo B
        await store.SaveAsync(CreateReceipt("repo-A"), CancellationToken.None); // A1
        await store.SaveAsync(CreateReceipt("repo-B"), CancellationToken.None); // B1
        await store.SaveAsync(CreateReceipt("repo-A"), CancellationToken.None); // A2
        await store.SaveAsync(CreateReceipt("repo-B"), CancellationToken.None); // B2
        await store.SaveAsync(CreateReceipt("repo-A"), CancellationToken.None); // A3

        var receiptsA = await store.LoadAllReceiptsAsync("repo-A");
        var receiptsB = await store.LoadAllReceiptsAsync("repo-B");

        Assert.Equal(3, receiptsA.Count);
        Assert.Equal(2, receiptsB.Count);

        Assert.Equal([1, 2, 3], receiptsA.Select(r => r.SequenceNumber));
        Assert.Equal([1, 2], receiptsB.Select(r => r.SequenceNumber));

        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.True(report.IsValid);
        Assert.Equal(5, report.TotalReceiptsVerified);
        Assert.Equal(2, report.Repositories.Count);
        Assert.All(report.Repositories, r => Assert.True(r.IsValid));
    }

    [Fact]
    public async Task ConcurrentReceiptWritesDoNotCorruptSequenceOrHashChaining()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        const int count = 10;

        // Save concurrently across multiple tasks
        var tasks = Enumerable.Range(0, count)
            .Select(_ => store.SaveAsync(CreateReceipt("repo-concurrent"), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        var receipts = await store.LoadAllReceiptsAsync("repo-concurrent");
        Assert.Equal(count, receipts.Count);

        // Verify sequence numbers form contiguous 1..10
        var sequences = receipts.Select(r => r.SequenceNumber).ToList();
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), sequences);

        // Verify complete cryptographic chain
        var report = await AuditLedgerVerifier.VerifyLedgerAsync(_directory);
        Assert.True(report.IsValid);
        Assert.Equal(count, report.TotalReceiptsVerified);
        Assert.Empty(report.AllAnomalies);
    }
}
