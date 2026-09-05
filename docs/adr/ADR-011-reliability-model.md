# ADR-011: Evidence-Based Health Model & Failure Classification

- Status: **Accepted & Implemented in Code**
- Date: **September 3, 2026**
- Scope: Operational health metrics, failure classification, retry budgets, and recovery readiness

---

## Context

Standard operational metrics (e.g. process uptime, 0 errors in the last 24 hours) fail to establish actual disaster recoverability. A backup service can run continuously and exit with code 0 while writing to corrupted pack files, holding invalid keys, or failing to retain independent recovery kits.

---

## Decision

1. **Evidence-Based Health Classification (`HealthVerdict` in `Fortiq.Monitoring`)**:
   - `Recoverable`: Backup is fresh (within threshold), repository structural check passed, and a restore has been proven recently.
   - `Unproven`: Backups are succeeding within threshold, but restore verification has not been proven or is stale.
   - `AtRisk`: Missing recovery kit, outdated backups violating threshold, or recent operation failure.
   - Aggregate health evaluated via `report.Worst` (defaulting to `Unproven` if no repositories are managed).
2. **Deterministic Failure Classification**:
   - Transient I/O and network transport timeouts: Retried with exponential backoff and randomized jitter.
   - Authentication failures (wrong password/mnemonic), path violations, or cryptographic data corruption: Fail closed immediately without retry loops.
3. **Decoupled Telemetry Files**:
   - Status is recorded in local file artifacts (`health.json` and Prometheus textfile `fortiq.prom`) via `HealthPublisher.cs`.
