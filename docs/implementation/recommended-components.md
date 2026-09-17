# Recommended Implementation Components (Non-Normative)

These are implementation candidates, not wire-format dependencies.

## HPKE

Current Rust `hpke` releases support:
- ML-KEM;
- MLKEM768-X25519 / X-Wing;
- SHAKE/TurboSHAKE KDFs;
- ChaCha20Poly1305 and AES-GCM.

Use behind a `CryptoProvider` trait because:
- PQ HPKE standardization is still evolving;
- the crate itself is not the protocol.

## ML-DSA

Prefer a provider with strong verification/audit posture.

Current ecosystem options include:
- libcrux ML-DSA, whose core arithmetic/serialization implementation documents formal verification;
- RustCrypto ML-DSA, which is convenient but currently documents that it has not been independently audited.

Run cross-provider known-answer tests where possible.

## Reed–Solomon

Do not couple wire format to one crate.

`reed-solomon-erasure` is mature and widely known, but its own documentation notes that erasure coding does not detect corrupted shards; FORTIQ therefore always hashes shards separately.

Keep `ErasureCoder` abstract so newer SIMD implementations can be benchmarked/reviewed.

## Local index

A pure-Rust ACID KV such as `redb` is a suitable cache/index candidate.

The index is disposable and not canonical.

## CBOR

Avoid relying on generic map serialization for signed records.

Use fixed-order arrays plus strict decoder limits.

Any library used must reproduce FORTIQ deterministic test vectors byte-for-byte.
