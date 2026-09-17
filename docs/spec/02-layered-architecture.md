# 02 — Layered Architecture

```text
L0  Transport Mesh
    libp2p / QUIC / Relay / Rendezvous / DCUtR

L1  Root Control Plane
    Genesis / Policy / Membership / Revocation / Recovery

L2  Segment & Capability Layer
    Client Segment / writer capabilities / quotas / key epochs

L3  Logical Event Layer
    ticket/chat/shell/file events

L4  EventPack / Snapshot Layer
    bounded immutable signed encrypted units

L5  Blob Layer
    encrypted file blocks / manifests

L6  Storage Layer
    replication / Reed–Solomon / placement / repair

L7  Synchronization Layer
    heads / anti-entropy / inventories / checkpoints

L8  Reducers / Materialized Views
    ticket state / chat / files / shell capability

L9  Product UI
    Client / Operator Workspace / diagnostics
```

Each layer has a narrow contract and can be tested independently.

Transport cannot grant application authority.

Storage cannot infer plaintext application semantics.

Reducers cannot bypass cryptographic authorization.
