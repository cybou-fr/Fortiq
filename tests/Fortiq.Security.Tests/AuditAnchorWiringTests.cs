using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Operations;

namespace Fortiq.Security.Tests;

/// <summary>
/// That a receipt written in production actually advances an anchor outside the receipt directory.
/// </summary>
/// <remarks>
/// The anchor types existed for some time with nothing constructing one. Every production receipt
/// store was built without an anchor while the README said heads were anchored externally, and no
/// test noticed, because every test that used an anchor supplied its own. A capability nothing
/// composes is not a control; these tests are about the composition, not the capability.
/// </remarks>
public sealed class AuditAnchorWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "fortiq-anchor-tests",
        Guid.NewGuid().ToString("N"));

    private readonly FortiqStatePaths _paths;

    public AuditAnchorWiringTests()
    {
        Directory.CreateDirectory(_root);
        _paths = FortiqStatePaths.Resolve(_root);
    }

    [Fact]
    public void TheAnchorDirectoryIsNotInsideTheReceiptDirectory()
    {
        // The whole point. An anchor inside the directory it attests to is rewritten by whoever
        // rewrites the receipts, and the two then agree with each other.
        var receipts = Path.GetFullPath(_paths.Receipts);
        var anchors = Path.GetFullPath(_paths.AuditAnchors);

        Assert.False(
            anchors.StartsWith(receipts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Ledger heads must be anchored outside the receipt directory they attest to.");
    }

    [Fact]
    public async Task WritingAReceiptThroughTheComposedAnchorRecordsTheHeadOutside()
    {
        var anchor = AuditAnchors.ForState(_paths);
        var store = new FileSystemOperationReceiptStore(_paths.Receipts, anchor);

        await store.SaveAsync(Backup("snapshot-1"), CancellationToken.None);
        await store.SaveAsync(Backup("snapshot-2"), CancellationToken.None);

        var anchorFile = Path.Combine(_paths.AuditAnchors, AuditAnchors.AnchorFileName);
        Assert.True(File.Exists(anchorFile), $"No anchor was written at '{anchorFile}'.");

        var entries = File.ReadAllLines(anchorFile)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<AuditAnchorEntry>(line)!)
            .ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("repository-1", entries[0].RepositoryId);

        // The heads advance in step with the chain. An anchor that recorded the same head twice, or
        // went backwards, would agree with a rewritten history rather than contradict one.
        Assert.Equal(entries.Select(entry => entry.SequenceNumber).OrderBy(number => number), entries.Select(entry => entry.SequenceNumber));
        Assert.True(entries[1].SequenceNumber > entries[0].SequenceNumber);
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ReceiptHash)));
    }

    [Fact]
    public async Task TheAnchoredHeadMatchesTheReceiptChainItAttestsTo()
    {
        var anchor = AuditAnchors.ForState(_paths);
        var store = new FileSystemOperationReceiptStore(_paths.Receipts, anchor);

        await store.SaveAsync(Backup("snapshot-1"), CancellationToken.None);
        await store.SaveAsync(Backup("snapshot-2"), CancellationToken.None);

        // Read back from the ledger the store maintains, which is the thing the anchor must agree
        // with. Comparing the anchor to a value the test computed itself would only prove the test
        // and the store use the same arithmetic.
        var ledger = await FileSystemOperationReceiptStore.LoadLedgerStateAsync(
            _paths.Receipts,
            "repository-1",
            CancellationToken.None);

        Assert.NotNull(ledger);

        var anchorFile = Path.Combine(_paths.AuditAnchors, AuditAnchors.AnchorFileName);
        var last = File.ReadAllLines(anchorFile)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<AuditAnchorEntry>(line)!)
            .Last();

        // If these could differ, the anchor would be a second opinion about a different history, and
        // a verifier comparing them would report tampering on every healthy machine.
        Assert.Equal(ledger!.SequenceNumber, last.SequenceNumber);
        Assert.Equal(ledger.LastReceiptHash, last.ReceiptHash);
    }

    private static OperationReceipt Backup(string snapshotId)
    {
        var completed = DateTimeOffset.UtcNow;

        return new OperationReceipt(
            Guid.NewGuid(),
            OperationKind.Backup,
            "repository-1",
            new EngineIdentity("restic", "0.19.1", new string('a', 64)),
            completed.AddMinutes(-1),
            completed,
            EngineResult.Succeeded,
            snapshotId,
            null,
            new Dictionary<string, long>(StringComparer.Ordinal) { ["bytesProcessed"] = 1024 },
            []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
