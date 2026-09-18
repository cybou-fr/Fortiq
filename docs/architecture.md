# FORTIQ — Architecture Specification (Canonical v3)

**Status:** Active architecture specification  
**Source of Truth:** `FORTIQ_CANONICAL_ARCH_v3` (supersedes Canonical Architecture v2 and prototype designs)  
**Date:** 2026-09-17  

---

## 1. Executive Summary & Core Philosophy

FORTIQ is a sovereign, post-quantum-resilient peer-to-peer support network composed of ordinary nodes. Rather than relying on a centralized server or a single privileged machine, FORTIQ represents its entire application state as an **immutable, signed, append-only object graph**, encrypted end-to-end, erasure-coded where appropriate, and distributed across storage-capable peers.

In FORTIQ:
- **The operator is not a machine.** The network owner and operator authority is rooted in signed Genesis and portable through a 24-word BIP-39 mnemonic. Any standard node can be temporarily unlocked as an operator workspace without changing its network transport identity.
- **Node PeerId is transport identity only.** Transport connections never confer administrative authority.
- **Every client has an independent cryptographic Segment.** The operator uses deterministic, distinct HPKE recipient keypairs for each client segment. A cryptographic compromise of Client A cannot decrypt data belonging to Client B.
- **Client owns the Ticket Access Epoch.** Remote terminal and administrative access is granted only by a locally valid `TicketAccessEpoch`. A client-signed close or revoke event immediately terminates active sessions and invalidates access locally, prior to distributed network convergence. Operator presentation overrides cannot resurrect a closed epoch.
- **Immutable storage with tiered durability.** Nobody edits an object in place. Small control records are highly replicated; medium state packs use bounded replication; large state packs and file blobs are encrypted first and Reed–Solomon erasure-coded second.

---

## 2. Hard Invariants

Canonical Architecture v3 defines 20 fundamental invariants that must be upheld across all components:

1. **Node PeerId is transport identity only.**
2. **Owner/Admin/Operator authority is rooted in signed Genesis.**
3. **No permanent operator machine exists.**
4. **A 24-word mnemonic unlocks temporary Operator Authority on any FORTIQ node without changing its PeerId.**
5. **Each client has an independent cryptographic Segment.**
6. **The operator uses a different deterministic HPKE recipient key for every client Segment.**
7. **A Segment key leak for Client A MUST NOT decrypt Client B.**
8. **One logical payload is encrypted once; recipients receive independent key envelopes.**
9. **High-volume chat/state is batched into small immutable EventPacks to amortize PQ signature and HPKE overhead.**
10. **Nobody edits an object in place, including Admin.**
11. **Ordinary actors create successor events/versions.**
12. **Admin may publish authoritative presentation overrides and tombstones, but cannot use them to bypass a client's local shell revocation.**
13. **Ticket access is controlled by a client-owned access epoch.**
14. **A client-signed close/revoke invalidates shell access immediately on that client, before distributed convergence.**
15. **Control-plane objects are highly replicated because they are tiny and needed for bootstrapping.**
16. **Large state packs and blobs are encrypted first and Reed–Solomon encoded second.**
17. **Shard integrity is verified independently before RS reconstruction.**
18. **No plaintext operator-wide database is canonical or required.**
19. **Local materialized views are disposable caches.**
20. **Protocol and cryptographic profiles are explicitly versioned and fail closed on downgrade.**

---

## 3. Layered Architecture (L0 – L9)

FORTIQ separates responsibilities into ten cleanly bounded layers. Each layer has a narrow contract, ensuring that transport cannot grant authority, storage cannot inspect plaintext, and reducers cannot bypass cryptographic authorization.

```text
+-------------------------------------------------------------------------+
| L9  Product UI (Slint Desktop, CLI, Operator Console, Client App)        |
+-------------------------------------------------------------------------+
| L8  Reducers & Materialized Views (Disposable Caches, Ticket State)    |
+-------------------------------------------------------------------------+
| L7  Synchronization Layer (Writer Streams, Heads, Anti-Entropy, Audits) |
+-------------------------------------------------------------------------+
| L6  Storage Layer (Replication, Streaming Reed–Solomon, Placement)      |
+-------------------------------------------------------------------------+
| L5  Blob Layer (Streaming AEAD, Chunking, Attachment Manifests)         |
+-------------------------------------------------------------------------+
| L4  EventPack & Snapshot Layer (Amortized PQ Envelopes, Snapshots)      |
+-------------------------------------------------------------------------+
| L3  Logical Event Layer (Tickets, Chat, File References, Epoch Grants)  |
+-------------------------------------------------------------------------+
| L2  Segment & Capability Layer (Client Segments, HPKE Recipient Keys)   |
+-------------------------------------------------------------------------+
| L1  Root Control Plane (Genesis, Policy, Enrollment, Revocations)       |
+-------------------------------------------------------------------------+
| L0  Transport Mesh (libp2p, QUIC, Circuit Relay v2, Rendezvous, DCUtR)   |
+-------------------------------------------------------------------------+
```

### L0 — Transport Mesh
- **Transport:** libp2p with native QUIC (`quic-v1`).
- **NAT Traversal & Routing:** Rendezvous protocol for registration/discovery in the `fortiq` namespace; Circuit Relay v2 reservations for restricted endpoints; DCUtR for automatic direct hole-punching.
- **Role:** Delivers authenticated byte streams between `PeerId`s. Transport authentication authenticates communication channels only, never application entities.

### L1 — Root Control Plane
- **Genesis Record:** Exactly one immutable Genesis per `NetworkId`. Pins `OwnerRoot` verification key, protocol baseline, initial storage quotas, and network policies.
- **GenesisId Derivation:** Computed as `hash(domain || CanonicalGenesisBody || Signature)`. Avoids circular hashing by separating `NetworkId` (random 256-bit seed) from `GenesisId`.
- **Enrollment & Invitations:** Signed single-use or scoped `JoinInvitation` records issue node and entity enrollment certificates.
- **Revocations:** Authoritative control-plane revocation lists signed by Owner Root.

### L2 — Segment & Capability Layer
- **Client Segment:** The primary cryptographic boundary for tenant data. Each client organisation or device group resides in a distinct `SegmentId`.
- **Operator Segment Keys:** Derived deterministically:
  $$\text{HPKE\_KeyPair} = \text{DeriveKeyPair}(\text{MasterSeed}, \text{NetworkId}, \text{SegmentId}, \text{KeyEpoch})$$
  No universal support-data decryption key exists. Compromising the operator's derived key for Segment A gives zero access to Segment B.
- **Segment Descriptors:** Signed by Owner Root, publishing the segment's authorized operator HPKE public key, client identity keys, and epoch counter.

### L3 — Logical Event Layer
- **Domain Events:** Fine-grained, immutable logical occurrences (e.g., `TicketCreated`, `TicketStateChanged`, `MessagePosted`, `FileAttached`, `AccessEpochGranted`, `AccessEpochRevoked`).
- **Identity & Attribution:** Each event is attributed to an `EntityId` and authenticated by that entity's signing key.
- **Causality:** Events reference parent event hashes or logical sequence counters within author streams.

### L4 — EventPack & Snapshot Layer
- **Overhead Amortization:** Hybrid post-quantum keys (ML-KEM-768 / X25519) and post-quantum signatures (ML-DSA-65) have substantial byte footprints. Encapsulating every 50-byte chat message individually would inflate network and storage overhead by 50–100x.
- **EventPacks:** Logical events sharing the same segment, author, and recipient set are buffered into an immutable `EventPack` (flushed after 100–250 ms, 32 events, or 64 KiB, with a 256 KiB maximum). One EventPack receives:
  - Exactly one post-quantum signature;
  - Exactly one payload encryption using a symmetric Data Encryption Key (DEK);
  - Exactly one set of recipient key envelopes.
- **Fast Revocation Exception:** Safety-critical events (such as `AccessEpochRevoked`) bypass buffering and flush immediately as a standalone single-event pack.
- **Encrypted Snapshots:** Periodic `SegmentSnapshot` objects incorporate the event frontier and reducer state, encrypted to segment recipients. Fresh or restarting nodes load the snapshot and only replay tail events, preventing unbounded cold-start replay.

### L5 — Blob Layer
- **Large Content:** Files and large attachments are chunked into fixed stripes (typically 1 MiB).
- **Encryption:** Each file generates a random `FileKey`. Stripes are encrypted using streaming AEAD (ChaCha20Poly1305) with unique per-stripe nonces.
- **Blob Manifests:** Detail root hashes, stripe counts, encryption metadata, and Reed–Solomon parameters. File-level envelopes wrap the `FileKey` directly for segment recipients.

### L6 — Storage Layer & Tiered Durability
Storage is tiered based on payload class and size:

| Storage Class | Payloads | Target Size | Durability Strategy |
| :--- | :--- | :--- | :--- |
| **CONTROL** | Genesis, Descriptors, Revocations, Policy | $\le 64\text{ KiB}$ | High direct replication across all storage nodes |
| **STATE_PACK** | Small EventPacks, Snapshot metadata | $< 64\text{ KiB}$ | Bounded replication (default 3 distinct peers) |
| **STATE_PACK (Large)** | Large EventPacks, Snapshots | $\ge 64\text{ KiB}$ | Streaming Reed–Solomon erasure coding |
| **BLOB** | Encrypted file stripes, attachments | Multi-MiB | Streaming Reed–Solomon erasure coding |

- **Decoupled Placement:** Content addressing (`ObjectId = hash(CanonicalObject)`) is strictly separated from storage placement. Shard placement is dynamic, recorded via signed placement records.
- **Integrity Rule:** Payloads are encrypted first and RS-coded second. Every shard carries an independent cryptographic hash verified prior to RS reconstruction.

### L7 — Synchronization Layer
- **Writer Streams & Heads:** Each author appends to their own sequential stream. Authors publish signed, ephemeral `HeadAdvertisement` records (replaceable routing hints).
- **Anti-Entropy:** Nodes exchange known head frontiers and object inventories, identifying and fetching missing tails via directed acyclic graph (DAG) walking.
- **Availability Audits & Repair:** Storage receipts are treated only as preliminary confirmations. Periodic challenge-response audits test real shard retrievability. When available shard count falls below the repair threshold, storage nodes trigger automated background re-encoding.

### L8 — Reducers & Materialized Views
- **Disposable Projections:** Application state (ticket status, unread counts, chat history, active attachments) is derived by pure reducer functions folding over verified event streams.
- **Zero Plaintext Database:** Local SQLite or key-value databases serve purely as disposable materialized view caches. Deleting the local cache causes zero data loss; the view is reconstructed by replaying decrypted canonical objects.
- **Memory-Only Operator Workspace:** When an operator unlocks authority via mnemonic on an untrusted or multi-user host, decrypted customer state is retained strictly in volatile, zeroized memory.

### L9 — Product UI & Local Daemon IPC
- **Thin Client / Fat Daemon:**
  - `fortiq-service`: Background system daemon (Windows Service or Linux systemd). Manages libp2p networking, encrypted storage, Quic streams, PTY process allocation, and verification.
  - `fortiq` (CLI) and `fortiq-desktop` (Slint GUI): Presentation frontends communicating exclusively over local IPC (Named Pipe `\\.\pipe\fortiq-ipc` on Windows; Unix Domain Socket `/run/fortiq.sock` on Linux).
- **Adaptive UI Modes:** The interface queries daemon state over IPC and renders role-appropriate workspaces (Managed Client view or Operator Console).

---

## 4. Identity & Key Lifecycle

```text
                             +------------------------+
                             |   24-Word BIP-39 Seed  |
                             +------------------------+
                                         |
                        +----------------+----------------+
                        |                                 |
                        v                                 v
          +---------------------------+     +---------------------------+
          |   Owner Root Signing Key  |     | Owner Segment Master Seed |
          |        (ML-DSA-65)        |     +---------------------------+
          +---------------------------+                   |
                        |                    +------------+------------+
                        v                    |            |            |
             Signed Genesis Record           v            v            v
             & Root Revocations          Client A      Client B     Client C
                                         Segment       Segment      Segment
                                           HPKE          HPKE         HPKE
```

1. **Owner Root Identity:** Root signing keypair generated from the mnemonic seed. Used exclusively for signing Genesis, initial policy, segment descriptors, and issuing operator session certificates. Never used for bulk data encryption.
2. **Deterministic Segment Derivation:** The `Owner Segment Master Seed` derives unique operator HPKE keypairs for each `SegmentId`. Compromise of one segment’s operator key does not affect other clients.
3. **Operator Session Certificates:** When an operator operates from a physical device, the Owner Root issues a bounded `OperatorSessionCertificate` delegating authority to an ephemeral local signing key. The current service uses a one-hour session TTL; lock, expiry, and replacement of a session cancel active terminal forwarding and wipe the volatile workspace. The seed phrase is immediately erased from memory.
4. **Node Transport Key:** Persistent Ed25519 keypair identifying the libp2p peer on the wire. Changing or replacing the host machine does not affect Genesis or Segment identity.

---

## 5. Ticket Lifecycle & Shell Safety Invariants

The support ticket is the central coordination entity for client assistance. Terminal access is strictly subordinate to client consent.

```text
[Client]                                                        [Operator]
   |                                                                |
   |-- 1. TicketCreated (State=OPEN, AccessEpoch=E_1) ------------->|
   |                                                                |
   |-- 2. Chat / File EventPacks (TicketCryptoEpoch_1) <===========>|
   |                                                                |
   |<-- 3. Shell Open Request (Epoch E_1, Session Cert) ------------|
   |                                                                |
   |-- 4. Verify Local Epoch E_1 & State in (OPEN, IN_PROGRESS) ----|
   |      (Launch ConPTY / PTY shell stream)                        |
   |                                                                |
  |-- 5. User closes/resolves the ticket --------------------------+
  |      a) Persist the lifecycle transition locally               |
  |      b) Terminate PTY child process tree immediately           |
  |      c) Revoke active shell streams                            |
  |      d) Replicate the accepted lifecycle event                 |
   |                                                                |
```

### Ticket Lifecycle Model
- Creating a ticket establishes the support authorization scope. There is no separate shell consent token or `remote_access_enabled` switch.
- Shell authorization requires:
  $$\text{TicketState} \in \{\text{OPEN}, \text{IN\_PROGRESS}\} \;\land\; \text{Valid Operator Session}$$
- The Client creates tickets, exchanges chat/files, and observes state. Operator/Admin sessions own lifecycle transitions.
- `RESOLVED` and `CLOSED` terminate active shell sessions. `RESOLVED -> IN_PROGRESS` is allowed; `CLOSED` is terminal and requires a new ticket.
- The managed node revalidates ticket lifecycle immediately before admitting `/fortiq/shell/next`. Legacy `/fortiq/shell/2.0` is rejected.

---

## 6. Cryptographic Profiles

The current development runtime uses the explicitly identified `FortiqClassicalDev1` profile. `FortiqPq1` / FORTIQ-PQ1 is reserved as the post-quantum target and is not yet deployed:

- **Key Encapsulation Mechanism (KEM):** Hybrid `ML-KEM-768` + `X25519` via HPKE (RFC 9180 profile).
- **Digital Signatures (SIG):** `ML-DSA-65` (FIPS 204).
- **Symmetric Encryption (AEAD):** `ChaCha20Poly1305` (RFC 8439) with 256-bit symmetric keys and 96-bit nonces.
- **Key Derivation (KDF):** HKDF-SHA256 / SHAKE256.
- **Cryptographic Hashing:** `BLAKE3` (default for high-throughput shard/content hashing) and `SHA-256` (canonical object IDs and Genesis).
- **Wire Serialization:** Strict Deterministic CBOR arrays (RFC 8949) adhering to Canonical Core schemas.

---

## 7. Migration and Legacy Coexistence

Existing libp2p transport infrastructure (QUIC, Relay v2, Rendezvous, DCUtR), native PTY/ConPTY streaming, and local IPC interfaces form the foundation for Layer 0, Layer 8, and Layer 9. The desktop presentation layer is implemented with Slint.

The transition from the legacy prototype (flat PeerId authorization, mutable SQLite storage) to the full Canonical Architecture v3 is managed across the 15 phases detailed in [docs/milestones.md](milestones.md) and [docs/spec/21-implementation-roadmap.md](spec/21-implementation-roadmap.md).


