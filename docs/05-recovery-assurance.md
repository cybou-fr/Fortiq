# Recovery Assurance

> **Implementation status: Partially implemented.** Levels 1, 2 and 4 exist, and Level 2 now runs on a schedule as well as on demand. Level 2b (sampled hash comparison against source) does not.


## Foundational Principle

The operational status `Backup succeeded` indicates nothing more than bytes written to a storage target. Fortiq treats recoverability as a completely separate discipline: **can the data be discovered, unlocked, read, and accurately restored on a clean system?**

A repository that has only performed backups, without having ever undergone a successful restoration test, is explicitly classified as **`Unproven`**, not `Healthy`.

---

## Recovery Confidence & Health Model

Recovery Confidence is computed strictly from deterministic, verifiable operational evidence—never from heuristics or AI sentiment:

- **RPO Freshness**: Elapsed duration since the last valid snapshot relative to target RPO;
- **Repository Integrity**: Outcome of the latest full cryptographic repository check (`check`);
- **Restore Verification Evidence**: Outcome of the most recent test restore (`ProvenRestore`), which restores the newest snapshot into disposable staging and reconciles the bytes written to disk against the byte count the engine reports. Drills run unattended on their own recurrence (`ScheduledDrillRunner`), so a repository on a machine nobody logs into can still become proven;
- **Sovereign Key Availability**: Validated existence of an independent offline recovery kit (`kit.json`);
- **Storage Immutability**: Active WORM retention status (e.g. S3 Object Lock compliance mode);
- **Policy Compliance**: Absence of security policy violations (such as plaintext transfers or non-isolated paths).

### Operational Health Verdicts (`HealthVerdict` in `Fortiq.Monitoring`)

1. **`Recoverable`**: Recent backup within threshold, repository integrity check verified recently, AND at least one proven restore verification on record.
2. **`Unproven`**: Backups are succeeding, but restore verification is missing or stale. (The UI displays a clear advisory warning rather than a positive recovery badge).
3. **`AtRisk`**: Condition present that would prevent recovery today: missing recovery kit, outdated backups exceeding threshold, or recent operation failure.

---

## Levels of Verification

1. **Level 1 — Metadata & Index Check**: Snapshots, pack file trees, and index files are structurally intact (`restic check`).
2. **Level 2 — Staged Restore Reconciliation** (*implemented*): The newest snapshot's source subtree is restored into private staging, and the total bytes on disk are reconciled exactly against the byte count the engine reports having written (`ProvenRestore`). Content integrity at this level is provided by the engine, which verifies blob hashes during restore and fails the operation on mismatch; Fortiq does not independently re-hash the restored files.
3. **Level 2b — Sampled Hash Comparison Against Source** (*design intent, not implemented*): A stratified sample of restored files compared against source hashes. This requires a source of truth for the expected hashes, which Fortiq does not currently record at backup time.
4. **Level 3 — End-to-End Application Proof**: Restored datasets are validated for application-level consistency without running untrusted binaries.
5. **Level 4 — Clean-Machine Recovery Drill**: Full isolated restoration executed via standalone `Fortiq.Recover` CLI on a secondary test machine.

---

## Evidence Logging

Every verification event generates a structured record (`OperationReceipt`). The record is written
atomically and is what monitoring reads; it is **not** cryptographically tamper-evident, and the
integrity protection it has today is the directory ACL. See [Spec 20](20-local-catalog-data-model.md)
and ADR-007.

Each receipt carries:
- Repository ID and snapshot identifier;
- Unlock method executed (without exposing secret keys);
- Restored byte count, file count, and duration;
- SHA-256 hash of the pinned engine binary;
- Detailed warning logs and remediation diagnostics.

Only a successful `restoreProof` receipt, recorded after reconciliation, establishes proof. A plain
`restore` receipt records engine completion and does not establish proof, including legacy records.
The complete verification records success, failure or cancellation; a failed evidence write cannot
return a durably proven result. Scheduled drill failures before engine execution are read from their
own schedule state and are not cleared by a later backup. A later successful proof resolves them.

`HealthPublisher` republishes `health.json` and `fortiq.prom` after the service pass or desktop proof
attempt. The desktop rejects positive claims from reports older than five minutes (or more than one
minute in the future) and refreshes every 30 seconds. A long running operation can also leave a report
stale; stale means current protection is unknown, not that the service is necessarily stopped.

---

## Test Execution Safety Invariants

- Restore testing MUST always target an isolated private staging directory (`RestoreStagingArea`).
- Restore testing MUST NEVER overwrite production live files.
- Restored executable binaries or scripts are NEVER automatically launched or executed.
- Verified staging trees are securely cleaned up upon test completion.
