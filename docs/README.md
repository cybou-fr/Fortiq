# FORTIQ Documentation Catalog

Welcome to the FORTIQ documentation suite. The architecture and protocol are defined by **Canonical Architecture v3**, establishing FORTIQ as a sovereign, post-quantum-resilient peer-to-peer support network.

---

## 1. High-Level Documentation

- **[Canonical v3 Charter & Invariants](CANONICAL_README.md)**: Original canonical v3 charter and core statement.
- **[Architecture Specification](architecture.md)**: System overview, L0–L9 layered architecture, 20 Hard Invariants, identity lifecycle, and ticket safety.
- **[Protocol Specification](protocol.md)**: Wire protocols (`/fortiq/*`), deterministic CBOR array encoding, FORTIQ-PQ1 crypto profile, and shard streaming.
- **[Milestones & Roadmap](milestones.md)**: Prototype baseline (M0–M15) and Canonical v3 Implementation Roadmap (Phases 1–15).
- **[Manual Verification Guide](manual-test.md)**: Step-by-step instructions for manual end-to-end testing of the daemon, CLI, desktop, and VPS relay.


---

## 2. Architecture Decision Records (ADRs)

All foundational architectural decisions are formally documented in dedicated ADRs:

| ADR | Title | Summary |
| :--- | :--- | :--- |
| **[ADR-001](adr/ADR-001-genesis-owner-signing-only.md)** | Genesis Owner Signing Only | Genesis pins Owner Root signing identity; no universal owner support-data HPKE decryption key. |
| **[ADR-002](adr/ADR-002-eventpacks.md)** | EventPacks | Amortizes post-quantum signature and HPKE envelope overhead across small event batches. |
| **[ADR-003](adr/ADR-003-tiered-storage.md)** | Tiered Storage | Separates storage into control replication, bounded small-state replication, and streaming RS. |
| **[ADR-004](adr/ADR-004-client-access-epoch.md)** | Client Access Epoch | Shell access is bound to client-owned access epoch; admin override cannot revive expired epochs. |
| **[ADR-005](adr/ADR-005-one-payload-n-envelopes.md)** | One Payload, N Envelopes | Payloads are encrypted once with a random DEK; recipients receive independent HPKE envelopes. |
| **[ADR-006](adr/ADR-006-placement-separate-from-content.md)** | Placement Separate from Content | Content addressing (`ObjectId`) is immutable and decoupled from dynamic shard placement. |

---

## 3. Canonical Architecture v3 Specifications

The deep technical specifications defining every aspect of the network:

1. **[00 — Architecture Review](spec/00-architecture-review.md)**: Vulnerability and bottleneck analysis of earlier drafts and their resolutions.
2. **[01 — Final Decisions](spec/01-final-decisions.md)**: High-level architectural consensus and core design rules.
3. **[02 — Layered Architecture](spec/02-layered-architecture.md)**: Layer 0 through Layer 9 contracts and boundary invariants.
4. **[03 — Genesis and Owner Key Lifecycle](spec/03-genesis-owner-key-lifecycle.md)**: Mnemonic derivation, Owner Root signing, and ephemeral session certificates.
5. **[04 — Entities, Segments and Capabilities](spec/04-entities-segments-capabilities.md)**: Client cryptographic segments, operator key derivation, and capabilities.
6. **[05 — Canonical CBOR and Object IDs](spec/05-canonical-cbor-object-ids.md)**: Strict deterministic array encoding, parser limits, and `ObjectId` derivation.
7. **[06 — Crypto Profile and Envelope Model](spec/06-crypto-profile-and-envelope-model.md)**: FORTIQ-PQ1 hybrid suite, ML-KEM-768, ML-DSA-65, ChaCha20Poly1305, and recipient privacy.
8. **[07 — Ticket Crypto Epochs and EventPacks](spec/07-ticket-crypto-epochs-eventpacks.md)**: Ticket-scoped cryptographic epochs, EventPack flushing rules, and envelope wrapping.
9. **[08 — State Versioning and Reducers](spec/08-state-versioning-reducers.md)**: Event sourcing, causality, writer streams, and deterministic projection reducers.
10. **[09 — Ticket State and Shell Safety](spec/09-ticket-state-and-shell-safety.md)**: Synchronous local access revocation, process termination guards, and access state machines.
11. **[10 — Storage Classes and Reed–Solomon](spec/10-storage-classes-reed-solomon.md)**: Tiered durability thresholds, erasure coding parameters, and shard checksumming.
12. **[11 — Placement, Repair and Availability](spec/11-placement-repair-availability.md)**: Placement records, retrieval challenges, availability audits, and automated healing.
13. **[12 — Sync, Heads and Anti-Entropy](spec/12-sync-heads-anti-entropy.md)**: Ephemeral head advertisements, inventory reconciliation, and DAG walking.
14. **[13 — Chat and Files](spec/13-chat-and-files.md)**: High-volume message delivery and streaming striped file transfers.
15. **[14 — Deletion, GC and Anti-Resurrection](spec/14-deletion-gc-anti-resurrection.md)**: Tombstone markers, `PurgeAuthorization`, garbage collection, and anti-resurrection checkpoints.
16. **[15 — Metadata Privacy](spec/15-metadata-privacy.md)**: Protection against traffic analysis, opaque identifiers, and encrypted metadata.
17. **[16 — Local Persistence and Caching](spec/16-local-persistence-caching.md)**: Disposable materialized view models and memory-only operator workspace policies.
18. **[17 — Protocol Map and Resource Limits](spec/17-protocol-map-resource-limits.md)**: RPC substream protocols, streaming shard protocol, and DoS buffer limits.
19. **[18 — Bootstrap and Enrollment](spec/18-bootstrap-enrollment.md)**: Node discovery, rendezvous bootstrapping, invitations, and enrollment flow.
20. **[19 — Snapshots, Search and Cold Start](spec/19-snapshots-search-cold-start.md)**: Encrypted segment snapshots for accelerated bootstrapping and state compaction.
21. **[20 — Threat Model](spec/20-threat-model.md)**: Comprehensive threat analysis, attack vectors, and security mitigations.
22. **[21 — Implementation Roadmap](spec/21-implementation-roadmap.md)**: 15-phase implementation plan transitioning to Canonical v3.
23. **[22 — Test Plan](spec/22-test-plan.md)**: Known-answer tests, cross-provider verification, chaos tests, and fuzzing suites.

---

## 4. Schemas, Implementation & Standards

- **[Core Records Schema](schemas/core-records.md)**: Canonical Rust structures for signed objects, recipient envelopes, EventPacks, and blob manifests.
- **[Ticket Reducer Schema](schemas/ticket-reducer.md)**: State machine and event reducer schema for ticket state transitions.
- **[Recommended Components](implementation/recommended-components.md)**: Trait architectures and recommended Rust library selections for post-quantum crypto, erasure coding, and serialization.
- **[Sources and Standards](SOURCES_AND_STANDARDS.md)**: References to FIPS 203, FIPS 204, RFC 9180 (HPKE), RFC 8949 (CBOR), and libp2p specifications.
- **[Manifest](MANIFEST.json)**: Machine-readable specification manifest and metadata.
