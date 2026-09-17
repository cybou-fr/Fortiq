# ADR-003 — Tiered Storage

**Decision:**

- control objects: high replication;
- small encrypted state: bounded replication;
- large state/blobs: Reed–Solomon.

**Reason:** RS for tiny records is operationally inefficient while full replication to every peer is unnecessary.
