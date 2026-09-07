# Specification 25: Backup Task, Trigger, Route & Run Model

> **Implementation status: read model implemented, execution unchanged.**  
> `BackupTask`, `Trigger` and `BackupRoute` exist in `src/Fortiq.CommunityModel` and are projected
> from existing schedules. `BackupSchedule` and the scheduled runners remain what actually runs, and
> the triggers here deliberately carry no occurrence arithmetic - that lives, tested, in the
> scheduler, and two implementations of daylight-saving handling would be one too many.
>
> One departure from §4 below: a `Route` also carries a drill trigger and a retention trigger.
> Schedules have both today, and a read model that dropped them would be quietly lossy.

## 1. Purpose

A **Backup Task** is the primary persistent declaration of backup behavior.

It answers:

> **Which data should be backed up, when should that happen, and which independent backup copies should be produced?**

A Task is not a Run. A Trigger is not a Task. A Route is not a Storage. A Repository is not a Task.

## 2. Backup Task

```json
{
  "id": "task-documents-daily",
  "name": "Documents Daily",
  "sources": ["source-documents", "source-projects"],
  "trigger": {
    "type": "dailyAt",
    "time": "02:30",
    "timeZone": "Europe/Paris"
  },
  "routes": ["route-documents-minio", "route-documents-usb"],
  "enabled": true
}
```

A Task owns source membership, trigger, enabled/paused state, catch-up/coalescing policy and route membership.

Route-specific encryption, storage and retention do not belong directly to the Task.

## 3. Trigger

A Trigger decides **when a Task occurrence is requested**.

Target trigger kinds:

### Manual

```json
{ "type": "manual" }
```

### Once

```json
{
  "type": "once",
  "at": "2026-09-07T20:00:00+02:00"
}
```

### Daily

```json
{
  "type": "dailyAt",
  "time": "02:30",
  "timeZone": "Europe/Paris"
}
```

### Weekly

```json
{
  "type": "weeklyAt",
  "days": ["Sunday"],
  "time": "04:00",
  "timeZone": "Europe/Paris"
}
```

### Monthly

```json
{
  "type": "monthlyAt",
  "day": 1,
  "time": "03:00",
  "timeZone": "Europe/Paris"
}
```

### File Change Watcher

```json
{
  "type": "fileChange",
  "settle": "00:05:00",
  "minimumInterval": "00:30:00"
}
```

File-change events MUST be coalesced.

```text
filesystem events
      ↓
mark task dirty
      ↓
settle window
      ↓
coalesce
      ↓
minimum interval check
      ↓
one Task Run
```

If changes occur during an active Run, the Task SHOULD remain dirty and schedule at most one additional run after completion.

## 4. Route

A Route describes **one independent backup copy produced by a Task**.

```json
{
  "id": "route-documents-minio",
  "storage": "storage-home-minio",
  "engine": "restic",
  "encryptionProfile": "enc-personal-recovery",
  "retention": {
    "keepDaily": 30,
    "keepMonthly": 12
  }
}
```

A Task with two Routes produces two independently observable copies.

## 5. Repository materialization

A Route resolves to a compatible Repository.

The resolver MAY reuse an existing Repository only if immutable compatibility properties match:

- engine/format;
- storage destination/namespace;
- encryption/access policy;
- repository binding rules.

Otherwise Fortiq creates a new Repository.

For Community v1 with restic:

> **One Repository SHOULD represent one encryption/access policy.**

## 6. Run

A Run is one occurrence of a Task.

```text
Task: Documents Daily
Occurrence: 2026-09-07 02:30
Run: 8a6b...
```

A Run contains one Route Run per selected Route.

## 7. Result aggregation

Route Runs MUST have independent outcomes.

```text
External SSD    ✓ Succeeded
Home MinIO      ✕ Network unavailable
```

The Task Run SHOULD report:

```text
Partially completed — 1 of 2 copies created
```

Suggested aggregate states:

- `Succeeded`
- `PartiallySucceeded`
- `Failed`
- `Cancelled`
- `Skipped`
- `Coalesced`

## 8. Concurrency

Different repositories MAY execute concurrently when local resource policy allows.

Repository-scoped conflict rules remain authoritative.

```text
backup repo-A + backup repo-B       allowed
backup repo-A + restore repo-B      allowed
retention repo-A + backup repo-A    serialized/refused
reconcile repo-A + repo-A writes    exclusive
```

The scheduler MUST NOT replace repository locking.

## 9. Catch-up

A machine off for seven days owes one backup of the current source state, not seven reconstructed historical backups.

Watcher triggers SHOULD coalesce.

One-shot triggers MUST never repeat after durable completion unless explicitly reset or cloned.

## 10. Recovery drills

Recovery testing is not a backup trigger.

A Task or Route MAY reference a Recovery Policy. The scheduler may materialize it into independent Recovery Runs.

Backup success MUST NOT overwrite recovery-test state.

## 11. Retention

Retention MAY differ by Route.

```text
Documents → USB
keep 30 daily copies

Documents → MinIO
keep 30 daily + 12 monthly + 5 yearly
```

Therefore retention belongs to Route/Repository policy, not Source.

## 12. Example tasks

### One-time external backup
Sources: Documents, Photos  
Trigger: Manual / once  
Route: External SSD

### Daily cloud backup
Sources: Documents, Projects, Finance  
Trigger: Daily 02:30  
Route: Home MinIO

### Weekly SFTP backup
Sources: Documents, Projects  
Trigger: Sunday 04:00  
Route: Company SFTP

### Monthly filesystem-level volume backup
Source: D:\  
Trigger: 1st day 03:00  
Route: Archive S3

This MUST NOT be described as bare-metal imaging.

### Active-project watcher
Source: D:\Projects\Active  
Trigger: File change  
Settle: 5 minutes  
Minimum interval: 30 minutes  
Route: MinIO
