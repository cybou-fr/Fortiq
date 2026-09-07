# Specification 27: Community Configuration Store & Migration

> **Implementation status: Design intent.**  
> Current `fortiq.backup-schedule` v1 files remain the implemented configuration format.

## 1. Goal

Introduce a decomposed Community configuration model without breaking:

- existing repositories;
- Recovery Kits;
- current backup schedules;
- recovery independence;
- repository IDs;
- receipts and evidence history.

Local configuration remains rebuildable and non-authoritative for disaster recovery.

## 2. Proposed local layout

```text
%ProgramData%\Fortiq\
├── config\
│   ├── sources\
│   ├── storages\
│   ├── identities\
│   ├── identity-keys\
│   ├── encryption-profiles\
│   ├── tasks\
│   └── routes\
│
├── runtime\
│   ├── repositories\
│   ├── task-state\
│   └── run-state\
│
├── credentials\
├── receipts\
├── audit\
└── health\
```

Exact directory names are implementation detail; conceptual separation is normative.

## 3. Secret placement

The following MUST NOT be stored in ordinary configuration JSON:

- storage secret keys;
- SFTP private keys unless protected by an approved secret store;
- private recipient keys;
- mnemonic phrases;
- plaintext EUS;
- plaintext engine repository passwords.

Configuration SHOULD store only references, public keys and fingerprints.

## 4. Compatibility adapter

During migration:

```text
legacy fortiq.backup-schedule v1
        ↓
LegacyScheduleAdapter
        ↓
Source + Storage + Task + Route projection
        ↓
existing execution core
```

This lets GUI and service adopt the new model without forcing repository recreation.

## 5. Legacy schedule mapping

Current fields:

```text
repository
kit
source
sourceStableId
recurrence
consistency
catchUp
enabled
drillRecurrence
retentionRecurrence
retention
prune
```

map to:

```text
Source
  path = source
  stableId = sourceStableId

Storage
  derived from repository locator/backend

Repository Binding
  existing repository + kit

Task
  sources = [Source]
  trigger = recurrence
  enabled = enabled
  catchUp = catchUp

Route
  repository = existing repository
  consistency = consistency
  retention = retention
  retention trigger = retentionRecurrence

Recovery Policy
  recurrence = drillRecurrence
```

No cryptographic material is regenerated during migration.

## 6. Stable IDs

New resource IDs MUST be independent from display names.

Renaming a Storage or Task MUST NOT recreate a repository.

Repository ID remains engine/repository identity and MUST NOT be reused as Task ID.

## 7. Resolver layer

Persistent user configuration SHOULD resolve into a minimal execution object:

```text
Resource Catalog + Task
          ↓
      Plan Resolver
          ↓
Backup Execution
{
  SourcePath,
  RepositoryDescriptor,
  Engine,
  StorageCredentialLease,
  EngineUnlockSession,
  Consistency
}
          ↓
existing Fortiq Operations / engine adapter
```

This limits churn in the security-critical execution core.

## 8. Health projection

Repository/Route health remains evidence-driven.

Task health is an aggregate projection.

```text
Task: Documents Daily

Route USB     Recoverable
Route MinIO   At risk
```

Task UI:

```text
Needs attention — 1 of 2 backup copies is at risk
```

The aggregate MUST NOT erase route-level evidence.

## 9. Receipts

Future receipt schemas SHOULD add stable references when available:

```text
taskId
routeId
sourceId
storageId
encryptionProfileId
```

Repository ID remains mandatory for repository operations.

Receipt evolution MUST remain versioned and backward-readable.

## 10. Migration phases

### Phase A — Documentation
Add Specs 24–27, ADR-016/017, index updates and compatibility notes.

### Phase B — Read model
Add resource/task types and project legacy schedules into them.

### Phase C — New writer
Write decomposed config documents while retaining legacy import.

### Phase D — Native execution
Scheduler consumes Tasks/Triggers/Routes directly.

### Phase E — Compatibility retention
Stop generating legacy schedules for new configuration, but continue reading/importing them and preserve recovery indefinitely.

## 11. Non-goals

This migration does not require:

- dynamic third-party plugins;
- changing restic repository format;
- re-encrypting existing backup payloads;
- changing Recovery Kit ownership;
- cloud control plane;
- bare-metal imaging;
- asymmetric recipient implementation before a separate crypto profile is accepted and implemented.
