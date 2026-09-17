# 14 — Deletion, Purge and Anti-Resurrection

## Tombstone

Admin-signed Tombstone is logical deletion.

Reducers stop presenting the target as active/canonical.

## PurgeAuthorization

Separate irreversible storage action.

A purge authorization references:
- TombstoneId;
- target object/blob/shard IDs;
- earliest GC time;
- admin signature.

## Why separate them

A mistaken UI delete should be reversible at the logical layer before physical purge.

Physical purge is explicit and delayed.

## Anti-resurrection

Do not delete the only evidence that something was deleted.

MVP:
- retain Tombstone control objects indefinitely.

Later:
- compact tombstones into signed deletion checkpoints / Merkle accumulators.

A stale peer must process current deletion control state before re-announcing old data.

## Client safety

Purging/tombstoning ticket events cannot recreate a dead client AccessEpoch.

Local client safety state remains authoritative for shell admission.
