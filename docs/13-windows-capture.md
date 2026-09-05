# Windows Capture: VSS & USN Change Journal

> **Implementation status: Partially implemented.** VSS via `--use-fs-snapshot` is implemented. **USN Change Journal inspection is not** — nothing in `src/` reads the journal.


## Objective

Fortiq reads source files from a stable point-in-time filesystem view and transparently reports the actual consistency level achieved. The Volume Shadow Copy Service (VSS) creates the consistent source view; the NTFS USN Change Journal assists change discovery without serving as the sole proof of snapshot completeness.

---

## Terminology

- **Requester**: Fortiq Windows Broker, managing the VSS backup lifecycle.
- **Writer**: OS or application component preparing its data for consistent backup (e.g. Microsoft Exchange, SQL Server, Hyper-V).
- **Provider**: VSS provider creating the shadow volume.
- **Shadow Set**: Coordinated set of shadow copies across one or more volumes.
- **Backup Components Document**: Requester selections and VSS operation results.
- **Writer Metadata Document**: Component definitions and restore semantics provided by writers.

---

## Source Consistency Model (`SourceConsistency` in `Fortiq.Application`)

| Consistency Mode | Flag / Execution | Guarantees & Failure Behavior |
| :--- | :--- | :--- |
| **`FileSystemSnapshot`** | Restic `--use-fs-snapshot` | Volume Shadow Copy Service creates a read-only point-in-time snapshot. Requires Windows backup privileges (`SeBackupPrivilege`). If privileges are missing, the operation **fails closed** (`SourceConsistencyException`) rather than silently downgrading. Recorded as `consistency: "snapshot"` in receipts. |
| **`Live`** | Standard direct read | Reads from the live filesystem. Files changing during read are captured as whatever was present at read time. Honest about non-point-in-time state. Recorded as `consistency: "live"` in receipts. |

---

## VSS State Machine

```text
Created
  → Initialized
  → WriterMetadataGathered
  → ComponentsSelected
  → SnapshotSetStarted
  → PreparedForBackup
  → SnapshotCreatedAndThawed
  → WriterStatusValidated
  → SourceLeaseIssued
  → EngineCopyRunning (restic --use-fs-snapshot)
  → BackupResultRecorded
  → BackupCompleteSignalled
  → SnapshotReleased
  → Completed
```

### Abort and Error Handling
- Failure during preparation triggers an immediate VSS abort signal.
- Orphaned snapshots are identified and safely reclaimed during service startup without deleting third-party shadow copies.
- Applications are frozen strictly during snapshot generation (typically 5–15 seconds), never for the entire backup transfer.

---

## NTFS USN Change Journal Integration

The USN Change Journal provides:
1. Rapid pre-backup change volume estimation;
2. Change-discovery acceleration for incremental evaluation;
3. Heuristic anomaly signals (e.g., massive file rename bursts indicative of ransomware);
4. Stratified selection of recently modified files for automated restore verification.

### Checkpoint Continuity & Invariants
- Each volume checkpoint tracks `VolumeSerial`, `JournalId`, and `NextUsn`.
- Before reading changes, Fortiq invokes `FSCTL_QUERY_USN_JOURNAL`.
- If journal truncation, journal deletion (`ERROR_JOURNAL_ENTRY_DELETED`), or ID changes are detected, Fortiq falls back to a deterministic **`FullScanRequired`** mode. Files are never omitted due to journal discontinuities.

---

## Security Invariants

- Only the privileged Windows platform component (`Fortiq.Platform.Windows`) opens raw volume handles.
- Desktop UI and service workers never receive raw disk handles.
- VSS device paths are validated against allowed system volume namespaces before execution.
