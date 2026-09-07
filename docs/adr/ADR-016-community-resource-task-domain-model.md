# ADR-016: Resource Catalog, Backup Tasks and Routes for Community

- Status: **Proposed**
- Date: **September 7, 2026**
- Scope: Community configuration/domain model

## Context

The original Community implementation is repository-centric. `BackupSchedule` combines repository location, recovery-kit path, source path, recurrence, source consistency, restore-drill recurrence and retention policy.

This representation is compact but does not scale cleanly to reusable storage endpoints, one source copied to several destinations, different source sets on different schedules, manual/one-shot backups, file-change watchers, route-specific encryption/retention, or independent route results.

## Decision

Adopt these persistent concepts:

```text
Source
Storage
Storage Credential
Engine
Identity
Identity Key
Encryption Profile
Backup Task
Trigger
Route
Repository
Run / Route Run
```

A Backup Task declares **what and when**.

A Route declares **where and under which engine/encryption/retention policy**.

A Repository is a materialized runtime/recovery boundary and is not the primary user-facing configuration object.

## Consequences

### Positive

- storage endpoints become reusable;
- one Task may produce several independent copies;
- S3/SFTP/filesystem can be represented without overloading repository strings;
- GUI terminology becomes user-centered;
- repository recovery invariants remain intact;
- scheduler policy and engine execution are separated.

### Costs

- local configuration becomes a graph rather than one schedule file;
- IDs/references require schema discipline;
- migration/compatibility adapter is required;
- task health must aggregate route/repository evidence.

## Compatibility

Existing `fortiq.backup-schedule` v1 files remain readable and map into the new model.

No existing repository, Recovery Kit or repository ID is invalidated.
