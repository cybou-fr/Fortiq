# FORTIQ — Milestones & Roadmap (Canonical v3)

**Status:** Active roadmap specification  
**Source of Truth:** `FORTIQ_CANONICAL_ARCH_v3` (Phases 1–15)  
**Date:** 2026-09-17  

---

## 1. Completed Foundational Milestones (Prototype Baseline)

The initial implementation phases established the production transport mesh, terminal streaming engine, local IPC architecture, and packaging foundation:

- **M0 — Workspace & Tooling:** Cargo workspace, documentation, formatting, and Clippy gates.
- **M1 — Identity Persistence:** Cryptographically generated and persisted Ed25519 node identity (`0600` permissions on Unix, SYSTEM/Admin ACLs on Windows).
- **M2 — Configuration Engine:** Configuration parsing and daemon mode resolution.
- **M3 — QUIC Transport Swarm:** Native QUIC (`quic-v1`), libp2p Identify, Ping, mutual HELLO handshake, and graceful shutdown.
- **M4 — Local Authorization:** Initial peer authorization check against configured credentials.
- **M5 — Shell Serving Prototype:** Platform shell selection (`pwsh`/`cmd` on Windows, `bash`/`sh` on Linux) and bidirectional streaming.
- **M6 — Ticket Gating:** Initial open/close support ticket lifecycle guarding shell streams.
- **M7 — Windows Process Handling:** Windows shell execution tree and process tree cleanup.
- **M8 — Rendezvous Discovery:** libp2p `/rendezvous/1.0.0` registration and peer discovery in the `fortiq` namespace.
- **M9 — Circuit Relay v2:** libp2p Circuit Relay v2 reservations, hop negotiation, and relayed stream routing.
- **M10 — DCUtR Direct Punching:** Automatic direct hole-punch upgrades on relayed connections with non-disruptive fallback.
- **M11 — Thin Client / Fat Daemon IPC:** Windows Named Pipes (`\\.\pipe\fortiq-ipc`) and Unix Domain Sockets (`/run/fortiq.sock`) bridging `fortiq-service` to GUI/CLI.
- **M12 — Native Pseudoterminal (PTY/ConPTY):** Integrated `portable-pty` with ConPTY / Unix openpty, binary `ShellFrame` framing, dynamic window resizing, and the Slint terminal pane.
- **M13 — System Services:** Native background services (`systemd` on Linux, Windows Service Control Manager via `windows-service`) and automated multi-node end-to-end integration tests.
- **M14 — Productization & Lightweight CLI:** Dedicated lightweight `fortiq` CLI binary with raw-mode terminal forwarding and automated Debian packaging (`.deb`).
- **M15 — Dual Windows Installers:** Separate Operator and Client NSIS setups with COMPUTERNAME provisioning, service boot auto-start, and desktop logon registration.

---

## 2. Canonical Architecture v3 Implementation Roadmap

Canonical Architecture v3 transitions FORTIQ from the prototype model to a sovereign, post-quantum-resilient, immutable object graph network. Implementation follows a disciplined 15-phase progression:

### Phase 1 — Canonical Core
- Deterministic CBOR serialization (RFC 8949) with fixed-order arrays.
- Structured identifiers: `NetworkId`, `OwnerId`, `EntityId`, `SegmentId`, `ObjectId`.
- Strict decoder resource limits (depth, length, memory bounds).
- Canonical signing interfaces and test vectors.

### Phase 2 — Genesis & Control Plane
- Root Genesis structure, GenesisId derivation (`hash(domain || TBS || sig)`), and signature verification.
- `JoinInvitation` and `EnrollmentCertificate` distribution.
- `SegmentDescriptor` management and distribution.
- Root revocation distribution and control plane replication.

### Phase 3 — Crypto Provider (FORTIQ-PQ1)
- **Status:** The runtime currently uses `FortiqClassicalDev1`; `FortiqPq1` remains reserved until the PQ provider and interoperability vectors are complete.
- Abstract `CryptoProvider` trait (decoupled wire format from specific Rust crates).
- Hybrid Post-Quantum HPKE (`MLKEM768-X25519`).
- Post-Quantum digital signatures (`ML-DSA-65`).
- Symmetric Data Encryption Key (DEK) encryption via `ChaCha20Poly1305`.
- Deterministic per-segment operator HPKE key derivation:
  $$\text{DeriveKeyPair}(\text{MasterSeed}, \text{NetworkId}, \text{SegmentId}, \text{KeyEpoch})$$
- Cryptographic zeroization of sensitive memory.
- Known-answer tests and cross-provider interoperability test vectors.

### Phase 4 — Local Event Graph
- Writer streams and sequence tracking.
- `EventPack` creation, buffering (100–250ms, 32 events, 64 KiB), and single PQ signature/envelope amortization.
- Immutable append-only storage model.
- Deterministic event reducers for ticket state.
- Client-owned `TicketAccessEpoch` management and local immediate revocation.
- Tombstone and `CanonicalHeadSet` tracking.

### Phase 5 — Ticket & Chat on Local Object Graph
- Migration of ticket-scoped chat and status events onto the local EventPack object graph.
- Verification of EventPack throughput, reducer performance, and snapshot generation without distributed network complexity.

### Phase 6 — Storage Engine & Erasure Coding
- Implementation of storage classes:
  - `CONTROL`: High direct replication ($\le 64\text{ KiB}$).
  - `STATE_PACK`: Bounded direct replication ($< 64\text{ KiB}$).
  - `BLOB` & Large State: Streaming Reed–Solomon erasure coding ($\ge 64\text{ KiB}$).
- Abstract `ErasureCoder` trait.
- Shard-level checksum verification (`BLAKE3-256`) before RS decoding.

### Phase 7 — Distributed Storage & Repair
- Object and Blob manifests.
- Dynamic storage placement decoupled from immutable content identity.
- High-throughput streaming shard transfer protocol (`/fortiq/shard/1`).
- Storage receipts and periodic challenge-response availability audits.
- Automated background shard repair.

### Phase 8 — Synchronization & Anti-Entropy
- Signed ephemeral `HeadAdvertisement` broadcasting (`/fortiq/head/1`).
- DAG walking and tail synchronization (`/fortiq/inventory/1`).
- Encrypted `SegmentSnapshot` objects for accelerated cold-start bootstrapping.

### Phase 9 — Files & Blob Streaming
- Ephemeral `FileKey` generation and streaming AEAD encryption.
- Reed–Solomon striped blob storage.
- Chunk resume and attachment manifests.

### Phase 10 — Shell & Session Binding
- **Status:** Canonical `/fortiq/shell/next` is implemented; legacy `/fortiq/shell/2.0` is rejected.
- Binding existing ConPTY/PTY shell streams to:
  - `OperatorSessionCertificate` (signed by mnemonic-derived Owner Root);
  - Valid client `TicketAccessEpoch`;
  - Cryptographic challenge-response handshake;
- Synchronous local shell termination on client `AccessEpochRevoked` event.

### Phase 11 — Portable Operator
- **Status:** Owner-signed session certificates, one-hour TTL, lock/expiry cancellation, and volatile workspace wipe are implemented.
- BIP-39 24-word mnemonic unlock on any standard node.
- Temporary Operator Session certificate derivation without changing host `PeerId`.
- Strictly volatile, zeroized in-memory decrypted workspace.
- Explicit session lock, timeout, and memory wipe.

### Phase 12 — Self-Support Loop
- Support for loopback intervention (`This Device`) using local IPC without traversing external swarm relays.

### Phase 13 — Deletion, Purge & Anti-Resurrection
- Tombstone records (logical deletion preserving deletion history).
- `PurgeAuthorization` records enabling physical garbage collection of ciphertext shards on compliant storage nodes.
- Anti-resurrection checkpoints preventing stale nodes from re-introducing purged state.

### Phase 14 — Legacy Retirement
- Formal deprecation and removal of:
  - Static configuration-derived `authorization.operator_peer_id`;
  - Permanent node roles (`OPERATOR` / `MANAGED`);
  - Legacy mutable SQLite ticket schemas in favor of pure event-sourced reducers.

### Phase 15 — Security Review & Verification
- Independent post-quantum cryptographic review.
- Protocol and CBOR parser fuzzing (AFL++ / libFuzzer).
- Multi-node network partition and chaos testing.
- Storage disk corruption and shard recovery tests.
- Complete dependency supply-chain audit.

