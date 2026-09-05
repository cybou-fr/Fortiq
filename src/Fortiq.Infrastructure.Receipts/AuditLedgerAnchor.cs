using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Fortiq.Infrastructure.Receipts;

/// <summary>
/// Machine-readable record anchored outside the user's mutable domain to prevent ledger history rewriting.
/// </summary>
public sealed record AuditAnchorEntry(
    string RepositoryId,
    long SequenceNumber,
    string ReceiptHash,
    DateTimeOffset Timestamp);

/// <summary>
/// Contract for anchoring the audit ledger head outside of the local writable receipts directory (ADR-007, Spec 15).
/// </summary>
public interface IAuditLedgerAnchor
{
    Task AnchorHeadAsync(string repositoryId, long sequenceNumber, string receiptHash, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op anchor for test scenarios or environments where external sink is unconfigured.
/// </summary>
public sealed class NullAuditAnchor : IAuditLedgerAnchor
{
    public static NullAuditAnchor Instance { get; } = new();

    public Task AnchorHeadAsync(string repositoryId, long sequenceNumber, string receiptHash, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Appends head updates to a dedicated append-only anchor file in a protected directory.
/// </summary>
public sealed class FileAuditAnchor : IAuditLedgerAnchor, IDisposable
{
    private readonly string _anchorFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAuditAnchor(string anchorFilePath)
    {
        _anchorFilePath = anchorFilePath ?? throw new ArgumentNullException(nameof(anchorFilePath));
    }

    public async Task AnchorHeadAsync(string repositoryId, long sequenceNumber, string receiptHash, CancellationToken cancellationToken = default)
    {
        var entry = new AuditAnchorEntry(repositoryId, sequenceNumber, receiptHash, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(entry) + Environment.NewLine;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var dir = Path.GetDirectoryName(_anchorFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.AppendAllTextAsync(_anchorFilePath, json, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

/// <summary>
/// Anchors head updates to the Windows ETW event tracing subsystem (Provider: "Fortiq-Audit").
/// Standard non-admin users cannot alter Windows ETW log records.
/// </summary>
[System.Diagnostics.Tracing.EventSource(Name = "Fortiq-Audit")]
public sealed class FortiqAuditSource : System.Diagnostics.Tracing.EventSource
{
    public static FortiqAuditSource Log { get; } = new();

    [System.Diagnostics.Tracing.Event(1, Message = "Audit ledger head: repo={0}, seq={1}, hash={2}", Level = System.Diagnostics.Tracing.EventLevel.Informational)]
    public void AuditLedgerHeadUpdated(string repositoryId, long sequenceNumber, string receiptHash)
    {
        if (IsEnabled())
        {
            WriteEvent(1, repositoryId, sequenceNumber, receiptHash);
        }
    }
}

/// <summary>
/// External audit anchor writing to ETW (Event Tracing for Windows).
/// </summary>
public sealed class EtwAuditAnchor : IAuditLedgerAnchor
{
    public static EtwAuditAnchor Instance { get; } = new();

    public Task AnchorHeadAsync(string repositoryId, long sequenceNumber, string receiptHash, CancellationToken cancellationToken = default)
    {
        FortiqAuditSource.Log.AuditLedgerHeadUpdated(repositoryId, sequenceNumber, receiptHash);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Chains multiple audit anchors together.
/// </summary>
public sealed class CompositeAuditAnchor : IAuditLedgerAnchor
{
    private readonly IReadOnlyList<IAuditLedgerAnchor> _anchors;

    public CompositeAuditAnchor(params IAuditLedgerAnchor[] anchors)
    {
        _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
    }

    public async Task AnchorHeadAsync(string repositoryId, long sequenceNumber, string receiptHash, CancellationToken cancellationToken = default)
    {
        foreach (var anchor in _anchors)
        {
            try
            {
                await anchor.AnchorHeadAsync(repositoryId, sequenceNumber, receiptHash, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Anchor '{anchor.GetType().Name}' failed: {ex.Message}");
            }
        }
    }
}
