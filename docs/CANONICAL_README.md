# FORTIQ — Canonical Architecture v4

**Status:** Active design source of truth  
**Date:** 2026-09-17  
**Supersedes:** Canonical Architecture v3, v2, and all earlier FORTIQ architecture drafts.

**v4 authority cut:** Ticket creation establishes the support scope. Shell
authorization requires an `OPEN` or `IN_PROGRESS` ticket plus a valid
Owner-signed Operator Session. `AccessEpoch`, `remote_access_enabled`, and
separate client revoke/grant commands are superseded and are not current shell
authority. See [ADR-007](adr/ADR-007-ticket-lifecycle-shell-authority.md).

FORTIQ is a sovereign support network composed of ordinary P2P nodes. Its application state is an immutable signed object graph, encrypted end-to-end, erasure-coded where appropriate, and distributed across storage-capable peers.

The operator is not a machine. The network owner/operator is rooted in Genesis and is portable through a 24-word mnemonic.

## Final architectural stack

```text
Existing libp2p transport
        ↓
Genesis / Owner Root / Network Policy
        ↓
Entity + Segment membership
        ↓
Immutable logical events
        ↓
EventPacks / Blob Manifests
        ↓
PQ-resistant encryption
        ↓
Ciphertext storage objects
        ↓
Replication or Reed–Solomon shards
        ↓
Anti-entropy / repair / GC
        ↓
Reducers / encrypted snapshots
        ↓
Tickets / Chat / Files / Shell / UI
```

## Hard invariants

1. Node PeerId is transport identity only.
2. Owner/Admin/Operator authority is rooted in signed Genesis.
3. No permanent operator machine exists.
4. A 24-word mnemonic unlocks temporary Operator Authority on any FORTIQ node without changing its PeerId.
5. Each client has an independent cryptographic Segment.
6. The operator uses a different deterministic HPKE recipient key for every client Segment.
7. A Segment key leak for Client A MUST NOT decrypt Client B.
8. One logical payload is encrypted once; recipients receive independent key envelopes.
9. High-volume chat/state is batched into small immutable EventPacks to amortize PQ signature and HPKE overhead.
10. Nobody edits an object in place, including Admin.
11. Ordinary actors create successor events/versions.
12. Admin may publish authoritative presentation overrides and tombstones, but cannot use them to bypass a client's local shell revocation.
13. Ticket access is controlled by a client-owned access epoch.
14. A client-signed close/revoke invalidates shell access immediately on that client, before distributed convergence.
15. Control-plane objects are highly replicated because they are tiny and needed for bootstrapping.
16. Large state packs and blobs are encrypted first and Reed–Solomon encoded second.
17. Shard integrity is verified independently before RS reconstruction.
18. No plaintext operator-wide database is canonical or required.
19. Local materialized views are disposable caches.
20. Protocol and cryptographic profiles are explicitly versioned and fail closed on downgrade.

Start with [`spec/00-architecture-review.md`](spec/00-architecture-review.md), then [`spec/01-final-decisions.md`](spec/01-final-decisions.md), or see the master index in [docs/README.md](README.md).

