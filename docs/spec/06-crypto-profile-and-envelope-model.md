# 06 — Crypto Profile and Envelope Model

**Implementation status:** The current runtime uses `FortiqClassicalDev1`. The
`FortiqPq1` profile described below is reserved for the future PQ provider and
must not be presented as deployed protection until its implementation and
interoperability vectors are complete.

## Standards basis

FORTIQ-PQ1 is designed around:
- ML-KEM from FIPS 203;
- ML-DSA from FIPS 204;
- HPKE with post-quantum / hybrid KEM extensions;
- 256-bit symmetric content keys.

The PQ HPKE extension is currently version-sensitive, therefore wire objects always carry an explicit `crypto_profile`.

## Default target

Balanced default:

```text
KEM  : MLKEM768-X25519
AEAD : ChaCha20Poly1305
SIG  : ML-DSA-65
```

KDF is pinned to the exact HPKE profile revision and covered by interoperability vectors.

## Sender authentication

PQ HPKE KEM profiles do not provide the old asymmetric authenticated KEM mode.

FORTIQ uses:
- HPKE for recipient confidentiality;
- ML-DSA signature for authorship/authority.

## One payload, N recipients

Normative invariant:

> FORTIQ MUST NOT store a separate full ciphertext copy per recipient.

```text
Payload
   ↓
random DEK
   ↓
AEAD encrypt once
   ↓
one ciphertext

DEK
 ├─ HPKE -> Recipient A
 ├─ HPKE -> Recipient B
 └─ HPKE -> Recipient N
```

## HPKE info

Every envelope binds at least:

```text
network_id
segment_id
object/purpose domain
recipient key id
key epoch
crypto profile
```

## Outer AAD

Payload AEAD binds:
- NetworkId;
- SegmentId;
- schema/version;
- pack/blob identifier;
- author key id;
- recipient-envelope-set digest.

## Recipient privacy

Outer envelopes identify recipients with Segment-scoped KeyIds rather than global human-readable identities.

A KeyId is derived from:
- SegmentId;
- recipient public key;
- epoch;
- domain.

## Key removal limitation

Removing a recipient from a future envelope cannot revoke plaintext they already decrypted.

Recipient removal only controls future access.
