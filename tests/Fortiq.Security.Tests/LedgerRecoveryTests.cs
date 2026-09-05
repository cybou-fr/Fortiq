using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;

namespace Fortiq.Security.Tests;

/// <summary>
/// What crash recovery adopts as the ledger head, and what it refuses to adopt.
/// </summary>
/// <remarks>
/// Recovery used to take the highest sequence number on disk and write its hash into the ledger as the
/// verified head. That trusts the tail without checking it: a receipt whose content had been altered
/// would be adopted, and the verifier would afterwards report the forgery as an established fact of
/// the history rather than as a break. Recovery would have signed off on it.
/// </remarks>
public sealed class LedgerRecoveryTests : IDisposable
{
    private const string RepositoryId = "repository-1";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "fortiq-ledger-recovery",
        Guid.NewGuid().ToString("N"));

    public LedgerRecoveryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task AnUncommittedReceiptIsAdoptedWhenItProvesItBelongs()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);

        // The machine stopped after the second receipt was written and before the ledger was
        // committed. This is the case recovery exists for, and it must still work.
        DeleteLedger();

        await store.SaveAsync(Backup(), CancellationToken.None);

        var ledger = await FileSystemOperationReceiptStore.LoadLedgerStateAsync(_directory, RepositoryId);
        Assert.Equal(3, ledger!.SequenceNumber);
    }

    [Fact]
    public async Task ATamperedReceiptIsNotAdoptedAsTheHead()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);

        DeleteLedger();
        AlterMetrics(sequence: 2);

        // Sequence 2 no longer hashes to what it records, so the walk stops at 1. Sequence 3 is left
        // on disk unadopted: it may be perfectly genuine, but it follows a receipt that is not, and a
        // head that skipped over the break would bless the whole tail.
        var recovered = await ReadRecoveredHeadAsync();
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task AReceiptWhosePreviousHashPointsElsewhereIsNotAdopted()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);

        DeleteLedger();
        RepointPrevious(sequence: 2);

        Assert.Equal(1, await ReadRecoveredHeadAsync());
    }

    [Fact]
    public async Task AGapInTheSequenceStopsTheWalkRatherThanBeingJumpedOver()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);

        DeleteLedger();
        DeleteReceipt(sequence: 2);

        // Taking the highest sequence would have adopted 3 and quietly accepted that 2 is gone. The
        // deletion is exactly what an audit ledger is for detecting.
        Assert.Equal(1, await ReadRecoveredHeadAsync());
    }

    [Fact]
    public async Task RecoveryDoesNotMoveTheHeadBackwards()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        await store.SaveAsync(Backup(), CancellationToken.None);
        await store.SaveAsync(Backup(), CancellationToken.None);

        var before = await FileSystemOperationReceiptStore.LoadLedgerStateAsync(_directory, RepositoryId);

        // A committed ledger with nothing new beside it. Recovery has nothing to do and must leave it
        // exactly where it is; rewinding would let a later write reuse a sequence number.
        await store.SaveAsync(Backup(), CancellationToken.None);

        var after = await FileSystemOperationReceiptStore.LoadLedgerStateAsync(_directory, RepositoryId);
        Assert.True(after!.SequenceNumber > before!.SequenceNumber);
        Assert.Equal(3, after.SequenceNumber);
    }

    /// <summary>Writes one more receipt and reports the head recovery settled on before writing it.</summary>
    private async Task<long> ReadRecoveredHeadAsync()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        var written = await store.SaveAsync(Backup(), CancellationToken.None);

        var ledger = await FileSystemOperationReceiptStore.LoadLedgerStateAsync(_directory, RepositoryId);
        Assert.NotNull(ledger);

        // The new receipt sits one past whatever recovery trusted, so the head it resumed from is the
        // ledger's sequence minus that one.
        _ = written;
        return ledger!.SequenceNumber - 1;
    }

    private void DeleteLedger() =>
        File.Delete(FileSystemOperationReceiptStore.GetLedgerPath(_directory, RepositoryId));

    private void DeleteReceipt(long sequence) => File.Delete(ReceiptPathAt(sequence));

    private void AlterMetrics(long sequence)
    {
        var path = ReceiptPathAt(sequence);
        var text = File.ReadAllText(path);

        // Content changed, hash left alone: the receipt now claims a digest its own body does not
        // produce, which is what a rewritten history looks like from the outside.
        File.WriteAllText(path, text.Replace("\"bytesProcessed\": 1024", "\"bytesProcessed\": 999999", StringComparison.Ordinal));
    }

    private void RepointPrevious(long sequence)
    {
        var path = ReceiptPathAt(sequence);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var previous = FindPreviousHash(document.RootElement)
            ?? throw new InvalidOperationException("The receipt records no previous hash.");

        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace(previous, new string('e', previous.Length), StringComparison.Ordinal));
    }

    private static string? FindPreviousHash(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("previousReceiptHash") && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }

            if (property.Value.ValueKind == JsonValueKind.Object && FindPreviousHash(property.Value) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private string ReceiptPathAt(long sequence)
    {
        foreach (var path in Directory.GetFiles(_directory, "*.json"))
        {
            if (path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var receipt = FileSystemOperationReceiptStore.LoadReceiptAsync(path).GetAwaiter().GetResult();
            if (receipt is not null && receipt.SequenceNumber == sequence)
            {
                return path;
            }
        }

        throw new InvalidOperationException($"No receipt at sequence {sequence}.");
    }

    private static OperationReceipt Backup()
    {
        var completed = DateTimeOffset.UtcNow;

        return new OperationReceipt(
            Guid.NewGuid(),
            OperationKind.Backup,
            RepositoryId,
            new EngineIdentity("restic", "0.19.1", new string('a', 64)),
            completed.AddMinutes(-1),
            completed,
            EngineResult.Succeeded,
            Guid.NewGuid().ToString("N"),
            null,
            new Dictionary<string, long>(StringComparer.Ordinal) { ["bytesProcessed"] = 1024 },
            []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
