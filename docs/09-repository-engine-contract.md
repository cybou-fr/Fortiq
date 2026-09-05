# Repository Engine Contract

> **Implementation status: Implemented.** `IRepositoryEngine` and the restic adapter and parsers exist and are covered by `Fortiq.Restic.ContractTests`.


## Objective

Fortiq uses external storage engines (restic 0.19.1 in V1) as modular execution workers, without attempting to hide fundamental differences between storage formats behind an artificial universal abstraction. The contract specifies the minimum capabilities required for enterprise sovereign recovery.

---

## Interface Definition

Implemented via `IRepositoryEngine` in `Fortiq.Application` (with engine adapter in `Fortiq.Infrastructure.Restic`):

```csharp
/// <summary>Everything a repository engine offers, as one composition root returns it.</summary>
public interface IRepositoryEngine : IBackupRepository, IRepositoryIdentityReader;

public interface IRepositoryIdentityReader
{
    Task<RepositoryId> ReadRepositoryIdAsync(RepositoryDescriptor repository, CancellationToken cancellationToken);
}

public interface IBackupRepository
{
    Task<RepositoryDescriptor> InitializeAsync(
        InitializeRepository command,
        CancellationToken cancellationToken);

    Task<BackupReceipt> CreateSnapshotAsync(
        CreateSnapshot command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(
        ListSnapshots query,
        CancellationToken cancellationToken);

    Task<CheckReceipt> CheckAsync(
        CheckRepository command,
        CancellationToken cancellationToken);

    Task<RestoreReceipt> RestoreAsync(
        RestoreSnapshot command,
        CancellationToken cancellationToken);

    Task ReconcileAsync(
        ReconcileRepository command,
        CancellationToken cancellationToken);

    Task<RetentionReceipt> ApplyRetentionAsync(
        ApplyRetention command,
        CancellationToken cancellationToken);
}
```

Every command implements `IOperationCommand` carrying a correlated `Guid OperationId`.

---

## Retention Policy & Protection Contract

Defined in `Fortiq.Application` (`Retention.cs`):

```csharp
public enum PruneMode
{
    ForgetOnly,
    ForgetAndPrune
}

public sealed record RetentionPolicy(
    int? KeepLast = null,
    int? KeepDaily = null,
    int? KeepWeekly = null,
    int? KeepMonthly = null,
    int? KeepYearly = null,
    TimeSpan? KeepWithin = null);

public sealed record ApplyRetention(
    RepositoryDescriptor Repository,
    RetentionPolicy Policy,
    PruneMode Prune = PruneMode.ForgetOnly,
    Guid OperationId = default) : IOperationCommand;
```

> [!IMPORTANT]
> **Zero-Snapshot Guard**: `ApplyRetentionAsync` validates retention plans before execution. If a policy would delete the last snapshot of any source, it throws `RetentionWouldRemoveEverythingException` to prevent accidental total data destruction.

Storage immutability (WORM, S3 Object Lock) is an end-to-end property of the storage provider, identity model, and retention policy—not of the engine alone. It is validated via dedicated storage inspection probes (`S3StorageProtectionInspector`).

---

## Division of Responsibilities

### Fortiq Platform Responsibility
- Scheduling, VSS snapshot lifecycle, and source consistency path selection;
- Ephemeral delivery of repository credentials via isolated Named Pipe broker (`Fortiq.PasswordHelper`);
- Pre-execution validation of engine binaries (SHA-256 manifest check, TOCTOU handle pinning);
- Policy approval and strict prohibition of dangerous/unverified retention actions;
- Normalization of engine events into structured audit receipts (`OperationReceipt`);
- Sandboxed staging areas (`RestoreStagingArea`) for test restores.

### Storage Engine Responsibility
- Block chunking, content deduplication, compression, and AES-256 repository encryption;
- Snapshot tree representation and content-addressed storage;
- Writing, reading, and cryptographic integrity verification of repository pack files;
- Platform-native restoration of files, timestamps, and extended attributes.

### Storage Backend Responsibility
- High durability and availability of storage objects;
- Transport-layer TLS encryption;
- Versioning and Object Lock immutability enforcement;
- Strict IAM segregation between daily write-only endpoint credentials and privileged administrative retention policies.

---

## External Process Execution & Safety

Implemented in `ResticProcessRunner`:
1. **Typed Argument Construction**: Commands are constructed using strongly-typed argument builders with zero shell interpolation.
2. **Zero Credential Exposure**: Passwords and keys NEVER appear in command-line arguments or process environment variables. Credentials pass strictly over a one-time ephemeral Named Pipe (`--password-command`).
3. **Deadlock-Free Streaming**: `stdout` and `stderr` streams are consumed asynchronously and parsed via deterministic JSONL event parsers (`ResticJsonParser`).
4. **Strict Schema Validation**: Parsing requires consistent exit codes and terminal summary events; progress events alone never synthesize a successful operation receipt.
5. **Controlled Cancellation**: Cancellation invokes graceful process termination (`Ctrl+C` / SIGINT signal) followed by constrained process tree termination to prevent repository lock corruption.
6. **Isolated Staging**: Restoration restores strictly to an isolated private staging directory, validates reparse points and path traversal, and promotes verified trees with an atomic rename.

---

## Retention & Prune Safety

- `PlanRetentionAsync` executes in dry-run mode without mutating data. A policy that would remove all existing snapshots for a source is rejected outright.
- `forget` (removing snapshots from the index) and `prune` (re-packing and deleting orphaned data blobs) are treated as distinct operations in code and in receipts (`snapshotsRemoved` vs `dataPruned`).
- Prune requires an exclusive repository lock (`Fortiq.Infrastructure.Runs`).
