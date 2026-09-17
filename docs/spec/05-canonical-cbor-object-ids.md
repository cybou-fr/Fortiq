# 05 — Canonical CBOR, Object IDs and Parsing

## Determinism

Hash/signature-critical structures MUST use deterministic CBOR.

For fixed protocol records, prefer arrays with fixed field order over maps.

Benefits:
- smaller encoding;
- no map ordering ambiguity;
- easier test vectors;
- easier strict decoding.

## Decoder limits

Every decoder must have hard limits:
- max input bytes;
- max nesting depth;
- max array length;
- max byte-string length;
- max text length;
- no indefinite-length containers in signed structures.

## Signing pattern

Never include ObjectId inside the bytes used to derive itself.

Pattern:

```text
TBS = canonical_cbor(header + ciphertext descriptor + ciphertext hash)
sig = Sign(author_key, domain || TBS)

ObjectId =
  SHA3-256("FORTIQ-OBJECT-ID-v1" || TBS || sig)
```

## Storage payload hash

Use a separate fast storage digest for shard/blob verification.

Recommended profile:

```text
protocol identity/hash : SHA3-256
storage shard checksum : BLAKE3-256
```

Hash algorithm identifiers are part of the storage profile.

## Plaintext hashes

Do not publish plaintext file hashes in outer metadata.

They leak equality.

If needed for user-visible verification, put them inside encrypted manifests.
