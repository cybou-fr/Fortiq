# 01 — Final Decisions

## Network root

- Exactly one Genesis per NetworkId.
- Genesis is immutable.
- Genesis pins Owner Root verification key, protocol baseline and initial policy.
- Genesis does **not** provide one universal HPKE key for all client support data.
- Owner == Admin == Operator for MVP.

## Identity

- Node PeerId: transport identity.
- Entity signing identity: application authorship.
- Client Segment HPKE key: confidentiality for one client Segment.
- Operator Segment HPKE key: deterministic, unique per client Segment.
- Operator Session key: short-lived authority on the current host.

## State

- Immutable append-only event/object model.
- No in-place update.
- Logical edit = successor event/version.
- Admin presentation override = separate signed object.
- Logical delete = Tombstone.
- Physical deletion = PurgeAuthorization + GC.

## Serialization

- Signed protocol structures use deterministic CBOR.
- Prefer fixed-order CBOR arrays for hash/signature-critical records.
- JSON is debug/export only.
- Duplicate keys / indefinite lengths / non-deterministic encodings are rejected.

## Crypto

The following is the canonical target profile. The current development runtime
uses the separately identified `FortiqClassicalDev1` profile while the PQ
provider remains under implementation.

Default target profile:

```text
FORTIQ-PQ1
KEM  : MLKEM768-X25519 hybrid HPKE
KDF  : pinned HPKE SHAKE/TurboSHAKE profile
AEAD : ChaCha20Poly1305
SIG  : ML-DSA-65
```

The exact KDF identifier is frozen with the final wire profile and test vectors.

The current PQ HPKE IETF work is still draft-level; FORTIQ therefore pins an explicit profile version and MUST support migration to the final RFC profile without reinterpretation of old ciphertext.

## High-volume state optimization

- Logical events are batched into author/segment EventPacks.
- Safety-critical client revocation events flush immediately.
- Ticket chat uses TicketCryptoEpoch to avoid two PQ-HPKE encapsulations for every tiny logical message.
- One payload is never stored as a full ciphertext copy per recipient.

## Storage

- Control plane: highly replicated.
- Small encrypted state: replicated to a bounded number of peers.
- Large StatePacks/blobs: Reed–Solomon.
- Always encrypt before RS.
- Always hash shards before accepting them for RS reconstruction.

## Ticket/shell

- Client creates ticket.
- Active client access epoch grants owner/operator shell capability while ticket state permits.
- Client close/revoke immediately invalidates the current access epoch.
- Operator cannot manufacture a new client access epoch.
- A client reopen creates a new epoch.
- Same-machine shell goes through local IPC.

## Local persistence

- Ciphertext/shards may be persisted.
- Local indexes/caches are not canonical.
- Decrypted operator-wide state is memory-only by default.
- No requirement for SQLite.
