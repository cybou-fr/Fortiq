using System.Collections.Concurrent;

namespace Fortiq.ControlPlane;

public interface IFleetHostRegistry
{
    Task RegisterHostAsync(HostIdentity host, CancellationToken cancellationToken = default);
    Task<HostIdentity?> GetHostAsync(string tenantId, string hostId, CancellationToken cancellationToken = default);
    Task<long> GetLastSequenceAsync(string tenantId, string hostId, CancellationToken cancellationToken = default);
    Task<bool> TryAdvanceSequenceAsync(string tenantId, string hostId, long expectedLastSequence, long newSequence, DateTimeOffset seenAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HostIdentity>> ListHostsAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory host registry managing enrolled endpoints and monotonic sequence states.
/// </summary>
public sealed class InMemoryFleetHostRegistry : IFleetHostRegistry
{
    private sealed class HostEntry
    {
        public required HostIdentity Host { get; init; }
        public long LastSequence { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public readonly object SyncRoot = new();
    }

    private readonly ConcurrentDictionary<string, HostEntry> _hosts = new(StringComparer.Ordinal);

    private static string MakeKey(string tenantId, string hostId) => $"{tenantId}:{hostId}";

    public Task RegisterHostAsync(HostIdentity host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();

        var key = MakeKey(host.TenantId, host.HostId);
        _hosts.AddOrUpdate(
            key,
            _ => new HostEntry { Host = host, LastSequence = 0, LastSeenAt = host.EnrolledAt },
            (_, existing) => new HostEntry { Host = host, LastSequence = existing.LastSequence, LastSeenAt = existing.LastSeenAt });

        return Task.CompletedTask;
    }

    public Task<HostIdentity?> GetHostAsync(string tenantId, string hostId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = MakeKey(tenantId, hostId);
        return Task.FromResult(_hosts.TryGetValue(key, out var entry) ? entry.Host : null);
    }

    public Task<long> GetLastSequenceAsync(string tenantId, string hostId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = MakeKey(tenantId, hostId);
        return Task.FromResult(_hosts.TryGetValue(key, out var entry) ? entry.LastSequence : -1L);
    }

    public Task<bool> TryAdvanceSequenceAsync(
        string tenantId,
        string hostId,
        long expectedLastSequence,
        long newSequence,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = MakeKey(tenantId, hostId);
        if (!_hosts.TryGetValue(key, out var entry))
        {
            return Task.FromResult(false);
        }

        lock (entry.SyncRoot)
        {
            if (entry.LastSequence == expectedLastSequence && newSequence > entry.LastSequence)
            {
                entry.LastSequence = newSequence;
                entry.LastSeenAt = seenAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    public Task<IReadOnlyList<HostIdentity>> ListHostsAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = _hosts.Values
            .Where(entry => string.Equals(entry.Host.TenantId, tenantId, StringComparison.Ordinal))
            .Select(entry => entry.Host)
            .ToArray();

        return Task.FromResult<IReadOnlyList<HostIdentity>>(list);
    }
}
