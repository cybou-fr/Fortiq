# Local Catalog & Data Model Architecture

> **Implementation status: Implemented.** `Fortiq.Infrastructure.Runs` provides the run registry and atomic registrations.


## Purpose & Foundational Invariant

The local catalog maintains configuration, scheduling state, job execution history, and derived index information about recovery points. It accelerates UI operations and background scheduling, but **is explicitly not the root of recovery trust**.

> [!IMPORTANT]
> **Catalog Independence Invariant**: Total loss or corruption of the local catalog database MUST NEVER prevent an operator from decrypting, reading, and restoring their data. Authoritative disaster recovery sources remain strictly:
> 1. The remote/local repository and its native snapshot metadata;
> 2. The self-contained recovery kit (`kit.json`) and cryptographic envelopes (`KeyEnvelopeV1`);
> 3. Autonomous offline restore CLI (`Fortiq.Recover`).

---

## Local Run Registry & Concurrency Control (`Fortiq.Infrastructure.Runs`)

Implemented in `FileSystemRepositoryRunRegistry.cs` and `FortiqRunDirectory.cs`:

### Concurrency Leases via OS File Handles
1. **Shared vs Exclusive Leases**:
   - Standard operations (backups, checks, snapshot queries) acquire **Shared** read repository leases (`FileShare.Read`).
   - Destructive, maintenance, or reconciliation operations (`reconcile`, `apply-retention` with prune) require **Exclusive** repository leases (`FileShare.None`).
2. **OS-Level Kernel Lock Binding**:
   - Concurrency locks are bound directly to active OS file streams on per-repository lock files (`<repository-id>.run`).
   - If a background worker or CLI process crashes abruptly, the operating system kernel immediately closes the handles and releases the held file lock.
   - Stale lock records, timeout polling heuristics, and "is owner alive" guessing games are eliminated entirely.
3. **Lock Rejection**:
   - Attempting an operation against a busy or exclusively locked repository throws `RepositoryBusyException` and exits deterministically (e.g. `Fortiq.Recover` exits with code 75 `ExitRepositoryBusy`).

---

## File-System Document Store Architecture

Rather than depending on an embedded database engine (e.g. SQLite) which can corrupt its single-file structure under ungraceful power loss or disk full conditions, Fortiq uses sovereign, atomic file-based document stores:

1. **Schedules & State (`Fortiq.Scheduling`)**:
   - `FileSystemScheduleStore` separates human-managed schedule definitions (`schedules/*.json` with schema `fortiq.backup-schedule` v1) from runtime state (`state/*.json`). Writing execution history never mutates configuration.
2. **Atomic Operation Receipts (`Fortiq.Infrastructure.Receipts`)**:
   - `FileSystemOperationReceiptStore` writes per-operation records (`receipts/<operation-id>.json` with schema `fortiq.operation-receipt` v1) using atomic write-and-rename (`.partial` → `.json`), so a reader never observes a partial record.
   - *Not tamper-evident.* The records carry no MAC, signature or hash chain; anyone able to write to the directory can alter or remove one undetectably. Today the protection is the directory ACL, not cryptography. ADR-007 specifies the ledger that would change this and is unimplemented.
   - **This is why the receipt path matters.** Every process on the machine reads and writes the same directory through `FortiqStatePaths`; two processes disagreeing about it once produced a health report built from half the evidence.
3. **Health Observability (`Fortiq.Monitoring`)**:
   - `HealthPublication` atomically writes `health.json` (schema `fortiq.health-report` v1) and `fortiq.prom` (Prometheus text format).
4. **Per-Directory File ACLs**: Located under `%ProgramData%\Fortiq\`. Rights differ by directory and there are three principals, not two: `SYSTEM` and the service SID for unattended work, and interactive operators for the parts the desktop reads and writes. `credentials\` is the one directory operators are excluded from. The layout and the reason are in [Spec 21](21-embedded-installer-and-updater.md); a single ACL over the whole tree locks the desktop out of its own state.

---

## Data Prohibitions (Forbidden In Local Storage)

Local file stores **MUST NEVER** store:
- Plaintext Engine Unlock Secrets (EUS), master passwords, or BIP-39 mnemonic strings;
- Reusable cloud storage secret keys or credentials;
- File payload bytes or unencrypted document text fragments;
- Raw AI conversation prompts or document embeddings.
