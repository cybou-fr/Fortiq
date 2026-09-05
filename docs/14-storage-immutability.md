# Storage Immutability & Ransomware Resilience

> **Implementation status: Implemented.** Object Lock inspection, retention policy and delete-marker recovery exist and are tested against MinIO.


## Objective

A compromise of a backup endpoint must never permit an adversary to destroy or truncate immutable backup history before detection and incident response can occur.

Encryption alone does not provide this protection: an attacker possessing valid write credentials can overwrite or delete encrypted blobs without ever knowing the decryption keys.

---

## Real-World S3 Object Lock Semantics & The Delete-Marker Vulnerability

Comprehensive testing against real S3-compliant storage engines revealed a critical nuance:
1. **Object Lock protects object *versions*, not object *keys***.
2. An unqualified `DELETE` request (specifying an object key without a `versionId`) succeeds with HTTP 200 and inserts a **Delete Marker**.
3. While all underlying data versions remain preserved on disk, subsequent standard `GET` or `LIST` calls perceive the repository as empty.
4. An adversary using compromised endpoint credentials can therefore make an entire repository appear deleted or corrupted without violating WORM retention.

### Automated Recovery: `S3HiddenObjectRecovery`
Fortiq directly resolves this vulnerability via `S3HiddenObjectRecovery` (`src/Fortiq.Infrastructure.ObjectStorage`):
- Automatically scans versioned bucket listings to identify keys whose newest version is a malicious delete marker with preserved data versions beneath.
- Safely removes the delete markers to unmask the intact data version.
- Intentionally ignores ephemeral lock objects: healthy repositories naturally place markers over unlocked locks upon completion; resurrecting lock markers would deadlock the engine.

---

## Object Lock Operational Modes

### Governance Mode
- Permissible for testing and flexible enterprise environments.
- Identities holding `s3:BypassGovernanceRetention` can truncate retention or purge versions.
- Endpoint and service accounts are strictly denied bypass rights.

### Compliance Mode
- Mandatory for high-security production deployments.
- No IAM principal (including AWS root or storage administrators) can shorten retention or delete versions prior to retention expiry.
- Storage provisioning (`RepositoryProvisioner`) verifies Object Lock configuration prior to initializing repositories; if WORM enforcement cannot be confirmed, provisioning fails closed.

---

## Identity & Permission Segregation

| Role / Identity | PutObject | Get/List | Simple Delete (Key) | DeleteObjectVersion | BypassGovernance |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Backup Endpoint** | Yes | Restricted | `locks/*` only | **DENIED** | **DENIED** |
| **Restore Worker** | No | Read-only | No | No | No |
| **Maintenance / Prune** | Yes | Yes | Approved scope | Expired/Unlocked only | **DENIED** |
| **Security Administrator** | Audited | Audit | Policy only | Separate Quorum | Quorum required |

---

## Pre-Provisioning Storage Capability Probes

Before accepting a bucket for active repository creation, Fortiq evaluates:
1. Bucket versioning enablement;
2. Active Object Lock configuration (`GetObjectLockConfiguration`);
3. Default retention mode and duration;
4. Explicit rejection of `DeleteObjectVersion` requests;
5. Behavior under unversioned `DELETE` operations.

If storage immutability cannot be verified at creation time, Fortiq refuses to mark the repository as protected.

---

## Noticing a Source Rewritten in Place

Immutable storage protects what is already stored. It does nothing about the next backup faithfully
recording an encrypted source, which is why `BackupAnomalyDetector` exists.

The number that would most obviously reveal encryption — how much was backed up — barely moves,
because encrypting files in place leaves the source almost exactly the same size. What moves is how
much of that was *new*: deduplication cannot help when every file differs from the one stored before
it, so added bytes jump towards the size of the whole source. Fortiq therefore records `data_added`
and the changed-file count on every backup receipt, and compares them against the repository's own
median rather than a threshold chosen in advance. The median, not the mean, so one earlier spike
cannot raise the bar enough to hide the next.

**Three deliberate refusals**, each fixed by a test:

- **It does not name a cause.** A person importing a video library produces exactly this shape.
  Findings state what was measured and by what multiple, nothing more. Telling somebody they have
  ransomware when they imported holiday footage teaches them to ignore the next alert.
- **It does not change the recoverability verdict.** The snapshots are all still there and still
  restorable; an unusual night is not a recovery problem, and reporting one would be false.
- **It does not act.** Nothing is paused, blocked or deleted. A backup tool that stopped backing up
  on a guess could be disarmed by an unusual Tuesday, and the encrypted files would still need
  backing up — that is what versioned, immutable storage and retention are for.

A repository needs more than four previous backups before any comparison is made. Below that there
is no history to be unusual against, and every repository would alarm through its first week.

---

## Retention Takes the Repository to Itself

Both retention modes now run exclusively, not only the one that deletes data.

Forgetting looked like bookkeeping — it removes snapshot references and leaves the data for a later
prune — so it ran alongside other work. Two things make that wrong:

- **The policy is applied to a list that can change underneath it.** `forget --keep-daily N` decides
  what to keep from the snapshots as they stand. A backup landing partway through that decision
  means the policy was applied to a repository that no longer exists.
- **It removes snapshots a restore may be about to read.** A drill chooses the newest snapshot and
  then restores it. If a forget ran in between, the restore fails and is recorded as a drill that
  could not prove recovery — a false alarm about the single thing this product exists to report on.

The second hazard survived making retention exclusive, because choosing a snapshot and restoring it
are two operations that each register a run of their own, leaving the repository unheld in between.
`ProvenRestore` therefore holds one shared run across both. Shared runs nest, so the operations
underneath still register normally.

A retention run that finds the repository busy fails fast with `RepositoryBusyException` rather than
waiting. That is the right shape for scheduled work: the attempt is recorded and the policy is
applied at its next occurrence, against a repository nothing else is touching.

---

## Retention Runs on a Schedule, and Only When Asked

Retention existed in the engine long before anything ran it: an installation kept every snapshot it
had ever taken. `ScheduledRetentionRunner` applies a policy on its own recurrence, and the design is
shaped by one asymmetry — a backup that does not run leaves yesterday's backup, a drill that does not
run leaves recovery unproven, and a retention run that goes wrong removes history that cannot be
brought back.

- **Opt-in, with an explicit policy.** A schedule with no `retentionRecurrence`, or with a recurrence
  and no `retention` block, is never due. A policy that keeps nothing is *refused* when the file is
  read rather than ignored: it is an instruction to delete every backup, and it must never be
  arrived at through a typo.
- **Exclusive in either mode.** Retention takes the repository through the run registry whether it
  prunes data or only forgets snapshots. Forgetting decides what to keep from the list as it stands,
  so a backup landing partway through applies the policy to a repository that no longer exists; and
  it can remove the snapshot a drill is restoring from, which would surface as recovery failing to
  prove — a false alarm caused by Fortiq's own housekeeping.
- **Busy means later.** A repository in use is left alone and the attempt is recorded, so the next
  tick does not ask again immediately. This is the one operation where eager retries are worse than
  waiting.
- **No catch-up.** A month offline owes one retention run, not four.
- **Last in the pass.** Backups, then drills, then retention, so a drill never loses its snapshot to
  housekeeping scheduled in the same minute.

The engine's refusal to leave a source with no snapshots at all remains the final line, and nothing
above replaces it.

The desktop wizard does **not** configure retention. Backups and a weekly drill are safe defaults;
deleting history is not something to switch on for somebody without asking.
