# 20 — Threat Model

## Malicious storage peer

Can:
- withhold;
- corrupt;
- lie about old receipt.

Cannot:
- forge shard hash;
- decrypt ciphertext;
- forge member/admin signatures.

Mitigation:
- hashes;
- k-of-n RS;
- retrieval audits;
- repair.

## Malicious enrolled client

Can generate valid signed writes under its capability.

Mitigation:
- Segment isolation;
- quota;
- rate limit;
- coarse capability;
- application reducer ACL.

It cannot delete historical network state without Admin authority.

## Client key compromise

Compromises that Client Segment according to key epoch.

Must not compromise other Segments.

## Operator Segment key compromise

Compromises only that Segment.

Must not allow derivation of OwnerSegmentMasterSeed or other Segment keys.

## Owner mnemonic compromise

Catastrophic for Owner authority and operator decryption across Segments.

Mitigation:
- careful mnemonic UX;
- short unlocked sessions;
- optional separate Recovery Root;
- root-key rotation architecture.

## Operator host compromise while unlocked

Host can use active operator capabilities and may steal in-memory Segment Master secret.

This cannot be cryptographically prevented once the user types mnemonic into a compromised OS.

## PQ profile downgrade

Never accepted silently.

CryptoProfile is signed/bound into Segment and object state.

## Fork/equivocation

A writer can fork its own append stream.

Both forks are retained and visible to reducer.

Security-sensitive client revoke wins locally.

## Deletion resurrection

Retained tombstone/deletion control prevents stale peer from reintroducing purged content into canonical state.
