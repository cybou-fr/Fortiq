# 04 — Entities, Segments and Capabilities

## Entity

An application identity has:
- signing verification key;
- optional base encryption key;
- capabilities.

A Node PeerId is not an EntityId.

## Client Segment

Every enrolled client gets a random 256-bit `SegmentId`.

Support data for that client belongs only to that Segment.

## Operator Segment HPKE key

The operator side is unique per Segment.

Conceptually:

```text
IKM =
  KDF(
    OwnerSegmentMasterSeed,
    "FORTIQ-OPERATOR-SEGMENT-v1" ||
    NetworkId ||
    SegmentId ||
    KeyEpoch
  )

(operator_sk, operator_pk) =
  HPKE.KEM.DeriveKeyPair(IKM)
```

The exact derivation is pinned by crypto profile test vectors.

## Client Segment HPKE key

Client generates a fresh HPKE recipient keypair for the Segment.

Private key never leaves the client.

## Segment Descriptor

Signed by Owner and accepted by Client:

```text
SegmentDescriptor [
  version,
  network_id,
  segment_id,
  owner_id,
  operator_segment_hpke_public_key,
  client_entity_id,
  client_signing_public_key,
  client_hpke_public_key,
  key_epoch,
  quota_profile,
  created_at,
  owner_signature,
  client_acceptance_signature
]
```

## Capability certificate

Storage admission should not need plaintext ticket semantics.

Enrollment grants coarse capabilities and quotas:

```text
WRITE_STATE
WRITE_BLOB
OPEN_TICKET
STORE_SHARDS
```

Storage nodes verify:
- valid membership;
- valid signature;
- object size/class;
- quota.

Application reducers enforce fine-grained ticket semantics after decryption.

## Node binding

A Client Entity may publish a signed `DeviceBinding` to its current PeerId.

Changing machine/PeerId does not require changing the Entity signing identity unless policy says so.
