# 00 — Architecture Review: v2 Bottlenecks and Resolutions

This document records the main weaknesses found in v2 and the decisions that resolve them.

## 1. Root HPKE key in Genesis created excessive blast radius

### Problem

v2 pinned `owner_hpke_pk` in Genesis and used it conceptually as the operator recipient.

That means compromise of one owner decryption key can expose every client.

It also conflicts with the previously accepted mandatory per-client segmentation rule.

### Resolution

Genesis pins Owner Root **signing identity**, not a universal support-data decryption key.

For every Client Segment:

```text
Owner Segment Master Seed
        +
NetworkId
        +
SegmentId
        +
Key Epoch
        ↓
HPKE DeriveKeyPair
        ↓
unique Operator Segment HPKE keypair
```

Client A and Client B never share operator-side HPKE key material.

The Segment Descriptor contains the segment-specific operator HPKE public key and is signed by Owner Root.

## 2. Per-message PQ overhead was too high

### Problem

A hybrid ML-KEM-768/X25519 HPKE encapsulation is about a kilobyte per recipient, and ML-DSA signatures are several kilobytes depending on parameter set.

Putting two HPKE envelopes plus one ML-DSA signature around every tiny chat message would make a 50-byte message several kilobytes on disk and wire.

### Resolution

FORTIQ distinguishes **logical events** from **signed encrypted storage objects**.

High-volume events are grouped by:
- Segment;
- author/session;
- recipient set.

into an immutable `EventPack`.

Default targets:

```text
flush after     : 100–250 ms
or event count  : 32
or plaintext    : 64 KiB
hard pack max   : 256 KiB
```

One pack gets:
- one PQ signature;
- one payload encryption;
- one recipient envelope set.

Safety-critical events such as client ticket close/revoke flush immediately as a single-event pack.

## 3. Reed–Solomon on every tiny object was inefficient

### Problem

Encoding every 1–5 KiB state object into many RS shards creates more metadata, connections and manifests than useful redundancy.

### Resolution

Three storage classes:

```text
CONTROL
  Genesis, membership, revocation, tombstones, policy
  -> high replication, no RS

STATE_PACK
  EventPacks / snapshots
  -> direct replication while tiny, RS above threshold

BLOB
  file/content stripes
  -> streaming RS
```

Default threshold:

```text
RS_MIN_BYTES = 64 KiB
```

Below it, encrypted objects are replicated to a small number of distinct peers (default 3), not to every peer.

This preserves distributed storage while avoiding pathological RS overhead.

Later, very small objects may be packed into immutable aggregate storage blocks and RS-coded.

## 4. CanonicalOverride was too powerful for safety

### Problem

A generic Admin override could theoretically hide a client close/revoke event and accidentally re-enable shell access.

### Resolution

Separate:
- presentation/state canonicalization;
- local safety authority.

Client shell access uses `TicketAccessEpoch`.

Admin canonicalization can affect display/reducer state but MUST NOT revive a client-invalidated access epoch.

Only a new client-signed reopen event creates a new valid access epoch.

## 5. Tombstone semantics were ambiguous

### Problem

"Delete" mixed:
- hide from logical state;
- stop replication;
- physically erase shards.

### Resolution

Two explicit admin operations:

```text
Tombstone
  logical deletion; immutable deletion record

PurgeAuthorization
  allows compliant storage nodes to physically garbage-collect shards
```

Tombstones (or compact deletion checkpoints) survive purge to prevent stale-peer resurrection.

## 6. Genesis / NetworkId derivation was underspecified

### Resolution

Use random `NetworkId` and a separate signed GenesisId.

Creation:

```text
NetworkId   = secure random 256-bit value
OwnerId     = hash(owner root verification key)
TBS         = canonical Genesis body
Signature   = OwnerRoot.Sign(TBS)
GenesisId   = hash(domain || TBS || Signature)
```

No circular hashing.

## 7. Storage receipts were not proof of availability

### Resolution

Receipts are only acknowledgements.

Availability is measured through periodic retrieval audits and real shard GET success.

Repair is triggered by observed reachable-shard count, not receipts alone.

## 8. Object discovery had no scalable head model

### Resolution

Use per-writer append streams plus signed ephemeral Head Advertisements.

Canonical objects stay immutable; Head Advertisement is a replaceable routing hint.

Anti-entropy compares known heads / object inventories and walks only missing tails.

## 9. Fresh Operator Workspace would replay too much history

### Resolution

Add encrypted `SegmentSnapshot` objects.

Snapshots:
- are optimization, never authority;
- include reducer version;
- include the incorporated event/object frontier;
- are encrypted to the same Segment recipients.

Fresh operator:
1. downloads latest valid snapshot;
2. verifies it;
3. loads only tail events.

## 10. Metadata leakage was larger than necessary

### Resolution

Outer storage metadata uses opaque IDs.

Ticket title, filename, message type, timestamps and system details stay inside ciphertext.

Storage routing sees only coarse object class, SegmentId/opaque writer key, sizes and hashes.

## 11. Client key rotation would require touching every historical message

### Resolution

Use ticket crypto epochs for high-volume streams.

`TicketCryptoEpoch` contains a random epoch key wrapped with PQ-HPKE for Client and Operator Segment.

Chat EventPacks derive/wrap pack keys under the active ticket epoch.

Files remain independently keyed and can use direct recipient envelopes because file-level HPKE overhead is negligible compared with file size.

## 12. PQ implementation maturity is a dependency risk

### Resolution

Create narrow traits:

```text
CryptoProvider
ErasureCoder
CanonicalCodec
LocalIndex
```

Wire format MUST NOT depend on a specific Rust crate.

Known-answer tests and cross-provider differential tests are mandatory.
