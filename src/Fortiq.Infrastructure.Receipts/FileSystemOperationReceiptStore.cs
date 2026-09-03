using System.Text.Json;
using System.Text.Json.Serialization;
using Fortiq.Application;

namespace Fortiq.Infrastructure.Receipts;

/// <summary>
/// Writes one JSON file per operation into a directory Fortiq owns. Receipts are written whole and
/// then moved into place, so a crash cannot leave a half-written file that looks like evidence.
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
            receipt.Result,
            receipt.SnapshotId,
            receipt.Source,
            receipt.Metrics,
            receipt.Warnings);

        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private sealed record ReceiptDocument(
        string Schema,
        int Version,
        Guid OperationId,
        OperationKind Operation,
        string RepositoryId,
        EngineIdentity Engine,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        OperationResult Result,
        string? SnapshotId,
        ReceiptSource? Source,
        IReadOnlyDictionary<string, long> Metrics,
        IReadOnlyList<string> Warnings);
}
