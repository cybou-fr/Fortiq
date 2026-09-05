using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Receipts;

/// <summary>
/// State tracking the highest sequence number and latest cryptographic receipt hash for a repository ledger.
/// </summary>
public sealed record LedgerState(long SequenceNumber, string LastReceiptHash);

/// <summary>
/// Writes one JSON file per operation into a directory Fortiq owns. Receipts are written whole and
/// then moved into place, so a crash cannot leave a half-written file that looks like evidence.
/// Automatically assigns monotonic sequence numbers and SHA-256 hash chaining per repository
/// to maintain an unbroken, tamper-evident audit ledger (ADR-007, Spec 15, DEC-013).
/// </summary>
public sealed class FileSystemOperationReceiptStore : IOperationReceiptStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;

    public FileSystemOperationReceiptStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task<string> SaveAsync(OperationReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Directory.CreateDirectory(_directory);

        using (await AcquireLockAsync(_directory, receipt.RepositoryId, cancellationToken))
        {
            var (currentSequence, currentLastHash) = await GetCurrentLedgerStateAsync(_directory, receipt.RepositoryId, cancellationToken);

            long nextSequence;
            string prevHash;
            string receiptHash;

            if (receipt.SequenceNumber > 0 && !string.IsNullOrWhiteSpace(receipt.ReceiptHash))
            {
                nextSequence = receipt.SequenceNumber;
                prevHash = receipt.PreviousReceiptHash ?? (nextSequence == 1 ? OperationReceipt.GenesisHash : currentLastHash);
                receiptHash = receipt.ReceiptHash;
            }
            else
            {
                nextSequence = currentSequence + 1;
                prevHash = nextSequence == 1 ? OperationReceipt.GenesisHash : currentLastHash;
                receiptHash = OperationReceipt.ComputeCanonicalHash(
                    receipt.OperationId,
                    receipt.Operation,
                    receipt.RepositoryId,
                    receipt.Engine,
                    receipt.StartedAt,
                    receipt.CompletedAt,
                    receipt.EngineResult,
                    receipt.SnapshotId,
                    receipt.Source,
                    receipt.Metrics,
                    receipt.Warnings,
                    nextSequence,
                    prevHash);
            }

            var path = Path.Combine(_directory, $"{receipt.OperationId:D}.json");
            var temporary = path + ".partial";
            var document = new ReceiptDocument(
                OperationReceipt.Schema,
                OperationReceipt.SchemaVersion,
                receipt.OperationId,
                receipt.Operation,
                receipt.RepositoryId,
                receipt.Engine,
                receipt.StartedAt,
                receipt.CompletedAt,
                receipt.EngineResult,
                receipt.SnapshotId,
                receipt.Source,
                receipt.Metrics,
                receipt.Warnings,
                nextSequence,
                prevHash,
                receiptHash);

            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);

            // Persist updated ledger state file
            if (nextSequence >= currentSequence)
            {
                var ledgerPath = GetLedgerPath(_directory, receipt.RepositoryId);
                var ledgerTemp = ledgerPath + ".partial";
                await using (var ledgerStream = new FileStream(ledgerTemp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(ledgerStream, new LedgerState(nextSequence, receiptHash), SerializerOptions, cancellationToken);
                }
                File.Move(ledgerTemp, ledgerPath, overwrite: true);
            }

            return path;
        }
    }

    public static string GetSafeRepositoryKey(string? repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) return "default";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(repositoryId.Length);
        foreach (var c in repositoryId)
        {
            sb.Append(invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c);
        }
        return sb.ToString();
    }

    public static string GetLedgerPath(string directory, string? repositoryId) =>
        Path.Combine(directory, $"{GetSafeRepositoryKey(repositoryId)}.ledger");

    private static string GetLockPath(string directory, string? repositoryId) =>
        Path.Combine(directory, $"{GetSafeRepositoryKey(repositoryId)}.lock");

    public static async Task<LedgerState?> LoadLedgerStateAsync(string directory, string? repositoryId, CancellationToken cancellationToken = default)
    {
        var ledgerPath = GetLedgerPath(directory, repositoryId);
        if (!File.Exists(ledgerPath)) return null;

        await using var stream = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<LedgerState>(stream, SerializerOptions, cancellationToken);
    }

    public static async Task<OperationReceipt?> LoadReceiptAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var doc = await JsonSerializer.DeserializeAsync<ReceiptDocument>(stream, SerializerOptions, cancellationToken);
        if (doc is null || doc.Schema != OperationReceipt.Schema) return null;

        return new OperationReceipt(
            doc.OperationId,
            doc.Operation,
            doc.RepositoryId,
            doc.Engine,
            doc.StartedAt,
            doc.CompletedAt,
            doc.EngineResult,
            doc.SnapshotId,
            doc.Source,
            doc.Metrics ?? new Dictionary<string, long>(StringComparer.Ordinal),
            doc.Warnings ?? Array.Empty<string>(),
            doc.SequenceNumber,
            doc.PreviousReceiptHash,
            doc.ReceiptHash);
    }

    public async Task<IReadOnlyList<OperationReceipt>> LoadAllReceiptsAsync(string? repositoryId = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory)) return Array.Empty<OperationReceipt>();

        var receipts = new List<OperationReceipt>();
        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            var receipt = await LoadReceiptAsync(file, cancellationToken);
            if (receipt is not null)
            {
                if (repositoryId is null || string.Equals(receipt.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                {
                    receipts.Add(receipt);
                }
            }
        }

        return receipts.OrderBy(r => r.SequenceNumber).ThenBy(r => r.StartedAt).ToList();
    }

    private static async Task<(long Sequence, string LastHash)> GetCurrentLedgerStateAsync(
        string directory,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var ledger = await LoadLedgerStateAsync(directory, repositoryId, cancellationToken);
        if (ledger is not null && ledger.SequenceNumber > 0 && !string.IsNullOrWhiteSpace(ledger.LastReceiptHash))
        {
            return (ledger.SequenceNumber, ledger.LastReceiptHash);
        }

        return await DiscoverLatestReceiptStateAsync(directory, repositoryId, cancellationToken);
    }

    private static async Task<(long Sequence, string LastHash)> DiscoverLatestReceiptStateAsync(
        string directory,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        long maxSequence = 0;
        string lastHash = OperationReceipt.GenesisHash;

        if (!Directory.Exists(directory)) return (0, lastHash);

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var receipt = await LoadReceiptAsync(file, cancellationToken);
                if (receipt is not null &&
                    string.Equals(receipt.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase) &&
                    receipt.SequenceNumber > maxSequence)
                {
                    maxSequence = receipt.SequenceNumber;
                    if (!string.IsNullOrWhiteSpace(receipt.ReceiptHash))
                    {
                        lastHash = receipt.ReceiptHash;
                    }
                }
            }
            catch
            {
                // Skip unparseable files during initial discovery
            }
        }

        return (maxSequence, lastHash);
    }

    private static async Task<IDisposable> AcquireLockAsync(string directory, string? repositoryId, CancellationToken cancellationToken)
    {
        var safeKey = GetSafeRepositoryKey(repositoryId);
        var inProcessLock = RepositoryLocks.GetOrAdd(Path.Combine(directory, safeKey), _ => new SemaphoreSlim(1, 1));
        await inProcessLock.WaitAsync(cancellationToken);

        FileStream? fileLock = null;
        var lockPath = GetLockPath(directory, repositoryId);
        try
        {
            var timeout = TimeSpan.FromSeconds(5);
            var start = DateTime.UtcNow;
            while (fileLock is null && (DateTime.UtcNow - start) < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    await Task.Delay(25, cancellationToken);
                }
            }

            fileLock ??= new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new CombinedLock(inProcessLock, fileLock, lockPath);
        }
        catch
        {
            fileLock?.Dispose();
            try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
            inProcessLock.Release();
            throw;
        }
    }

    private sealed class CombinedLock(SemaphoreSlim semaphore, FileStream fileStream, string lockPath) : IDisposable
    {
        public void Dispose()
        {
            fileStream.Dispose();
            try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
            semaphore.Release();
        }
    }

    public sealed record ReceiptDocument(
        string Schema,
        int Version,
        Guid OperationId,
        OperationKind Operation,
        string RepositoryId,
        EngineIdentity Engine,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        EngineResult EngineResult,
        string? SnapshotId,
        ReceiptSource? Source,
        IReadOnlyDictionary<string, long>? Metrics,
        IReadOnlyList<string>? Warnings,
        long SequenceNumber = 0,
        string? PreviousReceiptHash = null,
        string? ReceiptHash = null);
}
