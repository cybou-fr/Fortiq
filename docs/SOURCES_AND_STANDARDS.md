# Standards / External Basis

Architecture review was aligned against the following current standards/work:

- NIST FIPS 203 — ML-KEM, final 2024.
- NIST FIPS 204 — ML-DSA, final 2024.
- RFC 8949 — CBOR and deterministic encoding requirements.
- RFC 9180 / ongoing IETF HPKE work.
- `draft-ietf-hpke-pq-05` (July 2026) — PQ and PQ/traditional hybrid KEM profiles for HPKE, including MLKEM768-X25519.
- Current Rust HPKE ecosystem support includes PQ/hybrid KEMs, SHAKE/TurboSHAKE KDFs, and ChaCha20Poly1305/AES-GCM.
- Reed–Solomon implementation documentation explicitly requires an independent integrity mechanism for corrupted shards.

Protocol wire identifiers and crypto suites MUST be pinned by FORTIQ profiles/test vectors rather than inferred from library defaults.
