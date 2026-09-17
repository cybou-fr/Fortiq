# 21 — Implementation Roadmap

## Phase 1 — Canonical core

Implement:
- fixed-order deterministic CBOR records;
- NetworkId / OwnerId / EntityId / SegmentId;
- ObjectId;
- strict decoder limits;
- signing interface.

Deliver: test vectors.

## Phase 2 — Genesis and control plane

Implement:
- Genesis;
- Join Invitation;
- Enrollment;
- Segment Descriptor;
- Revocation;
- control replication.

## Phase 3 — Crypto provider

Implement `CryptoProvider`:
- PQ HPKE recipient envelope;
- ML-DSA signatures;
- DEK AEAD;
- segment derivation;
- zeroization.

Deliver:
- known-answer tests;
- cross-provider vectors.

## Phase 4 — Local event graph

Implement:
- writer streams;
- EventPack;
- immutable append;
- reducer;
- client AccessEpoch;
- Tombstone/CanonicalHeadSet.

No distributed RS yet.

## Phase 5 — Ticket/chat on local object graph

Move chat first, then ticket state.

Prove EventPack overhead, reducers, snapshots and UI without distributed complexity.

## Phase 6 — Storage engine

Implement storage classes:
- control replication;
- small state replication;
- RS blob/state.

Add `ErasureCoder` abstraction.

## Phase 7 — Distributed storage

Implement:
- manifests;
- placement;
- shard streaming;
- receipts;
- retrieval audits;
- repair.

## Phase 8 — Sync

Implement:
- head advertisements;
- tail sync;
- anti-entropy;
- encrypted snapshots.

## Phase 9 — Files

Implement:
- FileKey;
- streaming AEAD;
- RS stripes;
- resume;
- attachment manifests.

## Phase 10 — Shell

Bind existing PTY/ConPTY shell to:
- Owner Operator Session;
- Ticket AccessEpoch;
- challenge;
- immediate client revoke.

## Phase 11 — Portable operator

Mnemonic unlock on any node:
- root session certificate;
- Segment key derivation;
- memory-only decrypted workspace;
- lock/wipe.

## Phase 12 — Self-support

Add `This device` using local IPC.

## Phase 13 — Deletion/purge

Implement:
- Tombstone;
- PurgeAuthorization;
- anti-resurrection;
- GC.

## Phase 14 — Legacy retirement

Remove:
- permanent OPERATOR/MANAGED mode;
- operator_peer_id authority;
- mutable canonical SQLite ticket model.

## Phase 15 — Security review

Before production:
- independent crypto review;
- protocol fuzzing;
- parser fuzzing;
- multi-node chaos tests;
- disk corruption tests;
- dependency audit.
