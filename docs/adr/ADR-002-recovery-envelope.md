# ADR-002: Recovery Envelope & Key Derivation Specification

- Status: **Accepted & Implemented in Code** (Production Argon2 gate tracked in ADR-013)
- Date: **September 3, 2026**
- Scope: Engine Unlock Secret (EUS), recovery kit format, BIP-39, TPM, and envelope serialization

---

## Context

Restic manages an internal repository master key protected by password-derived key entries. Fortiq generates and protects a high-entropy 256-bit **Engine Unlock Secret (EUS)**, encoding it as `Base64UrlNoPadding(EUS)` when interfacing with restic.

Fortiq must support multiple independent, parallel unlock methods without requiring re-encryption of repository archives or relying on third-party custody.

---

## Decision

When provisioning a repository, Fortiq generates 256 bits of high-entropy randomness via a CSPRNG to establish the EUS.

The same EUS is encapsulated simultaneously within multiple independent cryptographic envelopes:

```text
Engine Unlock Secret (256 random bits)
├── PasswordEnvelopeV1 (Argon2id + AES-256-GCM)
├── Bip39RecoveryEnvelopeV1 (PBKDF2-HMAC-SHA512 + HKDF-SHA-256 + AES-256-GCM)
├── WindowsTpmEnvelopeV1 (Platform Crypto Provider + AES-256-GCM)
└── (Future) EnterpriseKmsEnvelopeV1 (Envelope encryption via Vault/KMS)
```

Enabling, revoking, or rotating an unlock method does not mutate the underlying encrypted repository blobs.

---

## Deterministic CBOR Envelope Format (`KeyEnvelopeV1`)

Serialized according to RFC 8949 (Deterministic CBOR):

```text
KeyEnvelopeV1 {
  schema:              "fortiq.key-envelope",
  version:             1,
  envelopeId:          16 random bytes (UUID v4),
  repositoryId:        32 bytes,
  engineId:            "restic",
  purpose:             "engine-unlock-secret",
  providerType:        password | bip39 | windows-tpm | enterprise-kms,
  suite:               algorithm suite identifier,
  providerParameters:  bounded map,
  wrappedSecret:       bounded byte string (AES-256-GCM ciphertext + tag),
  createdAt:           integer Unix timestamp
}
```

All public envelope metadata (`repositoryId`, `engineId`, `suite`, `envelopeId`) are bound as Authenticated Associated Data (AAD). An envelope cannot be transplanted into another repository.

---

## Envelope Implementations

### 1. `Bip39RecoveryEnvelopeV1` (`src/Fortiq.Infrastructure.Keys/Bip39RecoveryEnvelope.cs`)
- 24 words (256 bits of entropy) from standard English BIP-39 wordlist;
- Checksum and dictionary validity verified prior to key derivation;
- Seed derived via standard BIP-39 PBKDF2-HMAC-SHA512 (2048 iterations);
- KEK derived via HKDF-SHA-256 with Fortiq domain-separated context string;
- Plaintext EUS wrapped using AES-256-GCM with a fresh CSPRNG nonce.

### 2. `WindowsTpmEnvelopeV1` (`src/Fortiq.Infrastructure.Keys/WindowsTpmEnvelope.cs`)
- Non-exportable hardware key generated in TPM 2.0 via Microsoft Platform Crypto Provider (`ExportPolicy.None`);
- Stores public key SHA-256 fingerprint; key substitutions (e.g. system reinstall) are rejected before attempting unwrap;
- PCR binding is disabled by default to prevent benign firmware or UEFI updates from breaking daily unattended backups.

### 3. `RecoverySecretEnvelope` (`src/Fortiq.Infrastructure.Keys/RecoverySecretEnvelope.cs`)
- Direct unwrap from 256-bit raw recovery entropy via HKDF-SHA-256 + AES-256-GCM.

---

## Memory Zeroization Guarantee

All unwrapped key material is returned inside an `IKeyLease` (`BufferKeyLease`). Upon calling `Dispose()`, all internal byte buffers are actively overwritten with zeros (`CryptographicOperations.ZeroMemory`). Plaintext keys are never cast to immutable `System.String` instances.
