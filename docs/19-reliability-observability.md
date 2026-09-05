# Reliability, Observability & SLO Model

> **Implementation status: Implemented.** `Fortiq.Monitoring` publishes `health.json` and the Prometheus textfile.


## Operational Health Hierarchy

Fortiq distinguishes four fundamentally different operational achievements:
1. **Service Process Health**: The Windows Service is actively running and responsive.
2. **Backup Completion**: The backup engine process exited with code 0 and wrote data blocks.
3. **Repository Durability & Immutability**: Snapshots and pack trees are intact under WORM retention.
4. **Verifiable Recovery Assurance**: A staged restore completed and its byte count was reconciled with the engine output. Independent source-hash comparison remains design intent (Spec 05).

A healthy process status never masks a missing recovery verification. The aggregate health status is strictly determined by the **lowest common denominator** across required policy metrics.

---

## Service-Level Indicators (SLIs)

### 1. Backup Freshness Compliance (RPO)
```text
Protected backup sets with a valid snapshot newer than target RPO
──────────────────────────────────────────────────────────────────
       Total enabled backup sets scheduled within window
```

### 2. Verified Recovery Freshness (Restore SLA)
```text
Repositories with an authenticated restore proof within target SLA window
──────────────────────────────────────────────────────────────────────────
                    Total active managed repositories
```
A structural metadata integrity check (`check`) DOES NOT qualify as a restore proof.

### 3. Restore Success Rate (RTO)
Percentage of initiated restoration drills that complete within allocated RTO time budgets without integrity exceptions.

---

## File-Based Telemetry Architecture (`Fortiq.Monitoring`)

Saved telemetry expires. The desktop refreshes every 30 seconds and shows reports older than five
minutes as stale, suppressing positive repository verdicts. Prometheus consumers must require
`time() - fortiq_health_report_timestamp_seconds <= 300` before accepting saved health gauges, and
alert on an absent timestamp as well. Long operations may also delay publication; report staleness
does not establish whether the service process has stopped.

Only successful `restoreProof` receipts establish proof. Plain `restore` receipts, including legacy
ones, do not. A successful backup does not resolve failed verification; scheduled drill failures are
also read from their own state even if no restore receipt was produced.

Implemented in `HealthPublisher.cs` and `HealthPublication.cs`:
Rather than exposing an HTTP API endpoint (which fails to report health precisely when the service itself crashes), Fortiq writes decoupled status files to a local evidence directory:

1. **Structured Health JSON (`health.json`)**:
   - Machine-readable status consumed by `Fortiq.Desktop`.
   - Schema: `fortiq.health-report` (version 1).
   - Records repository verdicts (`recoverable`, `unproven`, `atRisk`), aggregate worst verdict, findings list, and underlying facts (timestamps, kit presence, storage immutability).
2. **Prometheus Textfile Collector (`fortiq.prom`)**:
   - Exported in standard Prometheus textfile format for automated node-exporter scraping:
   ```text
   # HELP fortiq_repository_recoverable Whether Fortiq can currently claim this repository is recoverable.
   # TYPE fortiq_repository_recoverable gauge
   fortiq_repository_recoverable{repository="7c8e2a1b"} 1

   # HELP fortiq_repository_last_backup_age_seconds Seconds since the last successful backup.
   # TYPE fortiq_repository_last_backup_age_seconds gauge
   fortiq_repository_last_backup_age_seconds{repository="7c8e2a1b"} 3600

   # HELP fortiq_repository_last_check_age_seconds Seconds since the last healthy integrity check.
   # TYPE fortiq_repository_last_check_age_seconds gauge
   fortiq_repository_last_check_age_seconds{repository="7c8e2a1b"} 86400

   # HELP fortiq_repository_last_restore_proof_age_seconds Seconds since a restore last proved recovery works.
   # TYPE fortiq_repository_last_restore_proof_age_seconds gauge
   fortiq_repository_last_restore_proof_age_seconds{repository="7c8e2a1b"} 172800

   # HELP fortiq_repository_storage_immutable Whether the storage keeps what is written to it.
   # TYPE fortiq_repository_storage_immutable gauge
   fortiq_repository_storage_immutable{repository="7c8e2a1b"} 1
   ```
   - Ages are exposed as seconds elapsed since the event occurred. If an event has never occurred, the metric is omitted entirely rather than reporting zero seconds ago (preventing "never" from being misread as "just now").

---

## Storage Protection Is Re-Checked, Not Remembered

Health once reported the protection recorded in the recovery kit when the repository was created.
A dashboard reads that as a statement about today, and it is not one: a bucket whose retention was
lifted last week still showed as immutable. Lifting retention is the first move of somebody
preparing to delete the backups, so the one moment the report most needed to change was the one
moment it could not.

The facts are now separated. `RepositoryFacts.StorageImmutable` is what the kit recorded at
provisioning; `StorageProtectionNow` is what the storage said when it was last asked, and it is the
one the verdict follows. Three outcomes:

| Provisioning | Now | Finding |
| :--- | :--- | :--- |
| Immutable | Immutable | none |
| Immutable | Not immutable | `storage-protection-lost` — a guarantee that was there and has been taken away |
| Immutable | Could not ask | `storage-protection-unknown` — unverified, stated as such |
| Not immutable | anything | `storage-not-immutable`, as before |

Two deliberate choices:

- **Unknown resolves to neither claim.** Turning "could not ask" into "not protected" cries wolf
  every time the network blinks. Turning it into "protected" is far worse: the report would go quiet
  exactly when somebody had removed the protection.
- **Losing protection does not make the verdict `AtRisk`.** The snapshots are all still there and
  still restore. Saying "may not be recoverable today" about data that restores fine would be false,
  and would teach whoever reads it that the worst verdict does not mean much.

## Scheduled Restore Drills

A repository becomes *proven* only when a restore has actually happened. Before drills existed, that
required a person, so an unattended machine stayed `Unproven` indefinitely: accurate, and of no help
to whoever depends on the backups.

`ScheduledDrillRunner` restores the newest snapshot into a disposable directory on its own
recurrence, reconciles what came back, and writes a restore receipt. Monitoring reads that receipt
like any other evidence; nothing about the health model needed to change.

Deliberate constraints:

- **Opt-in.** A schedule with no `drillRecurrence` gets no drills. A full restore of somebody's
  source is not a default to fall into because a field was omitted from a file.
- **No catch-up.** A machine that was off for a month owes one drill, not four. Each one restores
  the whole source, and four would prove the same thing at four times the cost.
- **Separate state.** Drill history is written under `<schedule>.drill`, apart from the backup's own
  state. Sharing it would make a failed drill look like an attempted backup, and a successful backup
  look like a proven restore.
- **A failed drill never stops backups.** A repository that cannot be restored today is precisely
  the one that must keep being backed up while somebody works out why.
- **Recorded attempts, not retries.** A failure moves the drill on to its next occurrence rather
  than restoring the entire source again on the next tick against a repository that has just shown
  it cannot be read. The last success is not erased by a failure: a repository restorable last week
  and not today has a different history from one never proven at all.
