# ADR-012: Recoverable File-System Local Document Store & Kernel Run Registry

- Status: **Accepted & Implemented**
- Date: **September 3, 2026**
- Scope: Local metadata storage, crash resiliency, and repository run concurrency control

---

## Context

Fortiq requires a crash-resilient, transactional local storage mechanism for scheduling state, policy configurations, execution history, and UI projections. 

Crucially, local storage **must never become an indispensable dependency for disaster recovery**. If the local machine is corrupted, destroyed by ransomware, or formatted, full restore must remain viable using only the repository archive, the recovery kit (`kit.json`), and the autonomous recovery CLI (`Fortiq.Recover`).

---

## Decision

1. **Rebuildable File-System Store Architecture**:
   - Local state is structured as atomic, human-readable JSON documents rather than a monolithic database file.
   - If local state is completely wiped, the repository remains fully discoverable and recoverable via native restic snapshots and `kit.json`.
2. **Separation of Configuration and State**:
   - `FileSystemScheduleStore` keeps schedule declarations (`schedules/*.json`) separate from dynamic run history (`state/*.json`). Writing runtime state never mutates configuration.
   - `FileSystemOperationReceiptStore` writes immutable per-operation records (`receipts/<operation-id>.json`) with atomic `.partial` rename.
3. **OS-Enforced File Descriptor Locks (`Fortiq.Infrastructure.Runs`)**:
   - Operational concurrency against repositories is managed by acquiring OS-level kernel file locks on designated lock files (`<repository-id>.run`).
   - Shared operations (`FileShare.Read`) permit concurrent backups/checks; exclusive operations (`FileShare.None`) serialize maintenance and reconciliation.
   - Process termination (clean or crash) triggers instant, automatic lock release by the operating system kernel.
4. **Strict Data Segregation**:
   - Raw master encryption keys, recovery secrets, and file payloads are strictly prohibited from storage in local files.
