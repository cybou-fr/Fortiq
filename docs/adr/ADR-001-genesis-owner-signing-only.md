# ADR-001 — Genesis Pins Owner Signing Root, Not a Universal Support HPKE Key

**Decision:** Client support encryption uses per-Segment operator HPKE keys derived from Owner Segment Master, not one `owner_hpke_pk` for all clients.

**Reason:** Mandatory blast-radius isolation.
