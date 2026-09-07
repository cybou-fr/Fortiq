# Documentation Audit — Community Domain Model

## Executive finding

The documentation is internally strong on recovery, repository security and evidence, but the configuration model is under-specified and overly repository-centric.

The current model makes the repository/schedule responsible for too many concerns at once:

- what is protected;
- where it is stored;
- which engine is used;
- which credentials reach storage;
- who can decrypt;
- what can unlock unattended writes;
- when backup runs;
- when drills run;
- when retention runs.

That is adequate for the present one-source/one-repository flow, but it does not naturally represent multi-destination Tasks, reusable storage endpoints, multiple identities, one-shot backup, watcher triggers or route-level failures.

## What must remain unchanged

- Repository remains the recovery/security boundary.
- Recovery Kit remains repository-scoped.
- Repository run locking remains repository-scoped.
- Storage credentials remain distinct from encryption secrets.
- Local catalog remains non-authoritative for recovery.
- Engine/storage responsibility separation remains valid.
- Recovery verdicts remain evidence-driven.

## Current structural problem

Conceptually, current configuration resembles:

```text
BackupSchedule
├── RepositoryLocation
├── KitDirectory
├── SourcePath
├── SourceStableId
├── Recurrence
├── Consistency
├── CatchUp
├── Enabled
├── DrillRecurrence
├── RetentionRecurrence
├── Retention
└── Prune
```

This is not merely a schedule. It is a combined source, destination, recovery and maintenance declaration.

## Target responsibility boundaries

| Entity | Answers | Must not own |
|---|---|---|
| Source | What data is protected? | storage, schedule, recipients |
| Storage | Where can bytes live? | repository secret, source selection |
| Storage Credential | How does Fortiq reach storage? | content decryption authority |
| Engine | Which repository format/worker is used? | identity, task trigger |
| Identity | Who/what may decrypt? | S3/SFTP login |
| Encryption Profile | Which recipients/writers form access policy? | storage endpoint |
| Repository | Concrete encrypted archive instance | user-facing schedule |
| Backup Task | What should happen and when? | engine internals |
| Route | Where/how one copy is produced | trigger ownership |
| Run | One execution occurrence | persistent policy |
| Recovery Kit | How to recover one Repository | scheduling |

## Restic constraint

For current restic-based Community architecture:

- repository encryption is repository-wide;
- recipient public keys may wrap a repository unlock secret;
- a recurring unattended writer must also be able to reopen that secret;
- public recipient keys alone cannot perform the next incremental backup;
- recipients and writers therefore need separate roles;
- materially different recipient policies should produce distinct repositories.

## Canonical vocabulary

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
Recovery Kit
Run
Route Run
Recovery Drill
Retention Policy
```

Do not use `Repository` as a synonym for a destination, protected folder, task or schedule.
