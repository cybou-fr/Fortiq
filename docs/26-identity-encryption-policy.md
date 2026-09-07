# Specification 26: Identity, Recipient & Writer Encryption Policy

> **Implementation status: Security design intent.**  
> Current TPM and BIP-39 repository envelopes remain the implemented mechanism. Public-key recipient identities, deterministic mnemonic-derived recipient keys and generalized encryption profiles described here are proposed and MUST NOT be documented as implemented until code and stable test vectors exist.

## 1. Purpose

Fortiq MUST distinguish:

1. **who may recover/decrypt backup data**;
2. **what may unlock a repository for recurring unattended writes**;
3. **who may access the storage transport**.

```text
Recipient Identity   → may decrypt/recover
Writer Identity      → may unlock for recurring backup
Storage Credential   → may reach storage bytes
```

These authorities MUST NOT be conflated.

## 2. Identity

An Identity is a stable principal such as:

- a person;
- an offline recovery mechanism;
- a device;
- an organizational recovery role.

An Identity MAY own multiple keys for rotation, device separation, custody separation and retirement/revocation.

## 3. Identity Key

Public Identity Key metadata MAY be stored locally if it contains no private material.

```json
{
  "id": "identity-key-alice-2026",
  "identity": "identity-alice",
  "role": "recipient",
  "algorithm": "proposed-asymmetric-recipient-profile",
  "publicKey": "<public-key>",
  "fingerprint": "<sha256>",
  "status": "active"
}
```

Lifecycle states SHOULD include `active`, `retired`, and `revoked`.

Private keys MUST NOT be inferred to exist on the backup endpoint merely because public metadata is configured.

## 4. Recipients

A Recipient is an Identity Key for which the repository EUS is wrapped.

```text
random repository EUS
      │
      ├── wrap → Alice public key
      ├── wrap → Emergency Recovery public key
      └── wrap → Company Recovery public key
```

The repository stores ciphertext envelopes and public metadata only.

A Recipient public key is sufficient to create a new envelope. It is not sufficient to unwrap the EUS.

## 5. Writers

Recurring backup requires a Writer capable of reopening the existing repository.

For current restic-based Community architecture:

```text
public recipient key only
       ✕
cannot reopen EUS tomorrow
```

Therefore recurring Routes need at least one Writer mechanism such as:

- this machine's TPM/device-bound key;
- future HSM/KMS writer;
- attended recovery-key session.

Example:

```text
EUS
├── Recipient: Alice public key
├── Recipient: Paper Recovery identity
└── Writer: This PC TPM
```

Loss of the Writer MUST NOT destroy sovereign recovery if an independent Recipient remains usable.

## 6. One-shot public-key backup mode

A one-shot backup MAY operate with recipient public keys without retaining long-term writer authority:

```text
generate EUS
↓
initialize repository
↓
perform one backup
↓
wrap EUS to recipient public keys
↓
discard plaintext EUS and temporary writer state
```

After completion, the originating machine need not retain recovery authority.

This mode MUST be explicitly distinguished from recurring backup.

## 7. Encryption Profile

An Encryption Profile is reusable policy:

```json
{
  "id": "enc-personal-recovery",
  "name": "Personal Recovery",
  "recipients": [
    "identity-key-alice-2026",
    "identity-key-paper-recovery"
  ],
  "writers": [
    "identity-key-this-pc-tpm"
  ]
}
```

Routes reference Encryption Profiles.

Changing a Profile does not automatically imply that an existing engine repository can express the new policy.

## 8. Restic repository-wide access constraint

Current restic repositories use repository-wide key material.

Community v1 MUST NOT claim per-snapshot recipient isolation inside one repository.

If two Routes require materially different access policies, Fortiq SHOULD materialize separate repositories.

## 9. Mnemonic-derived Identity

A future mnemonic-derived recipient identity MAY derive a stable asymmetric private key from recovery entropy.

If implemented, derivation MUST be:

- deterministic;
- versioned;
- domain-separated;
- covered by stable published test vectors;
- independent from UI wording;
- migration-safe.

Example context:

```text
fortiq/identity/recipient/x25519/v1
```

The exact cryptographic construction is not decided by this document.

## 10. Cryptographic construction policy

Fortiq MUST NOT invent a bespoke public-key encryption primitive.

A future asymmetric recipient envelope SHOULD use a standard reviewed construction such as an HPKE profile with an approved KEM, subject to a dedicated cryptographic ADR and test vectors.

This specification defines roles and lifecycle, not final algorithms.

## 11. Key rotation

Adding a new recipient key SHOULD create a supplementary envelope.

Removing/retiring a key SHOULD not mutate repository payload data when envelope-only rotation is sufficient.

A key MUST NOT be removed as the last sovereign recovery path until a replacement has been proven through recovery.

## 12. Recovery Kit implications

Recovery Kit metadata SHOULD enumerate:

- repository ID;
- engine;
- recipient envelope IDs;
- recipient public-key fingerprints/identity labels;
- required recovery material type;
- storage locator.

It MUST NOT include:

- private recipient keys;
- mnemonic words;
- plaintext EUS;
- reusable storage secret credentials.

## 13. Threat separation

| Compromise | Expected consequence |
|---|---|
| Storage credential stolen | Storage may be read/modified according to backend policy; plaintext remains encrypted |
| Recipient public key stolen | No decryption authority gained |
| Recipient private key stolen | Repositories encrypted to that key may be recoverable by attacker |
| Writer device key compromised | Recurring repository may be opened by that writer; independent recipients still control disaster recovery |
| Local catalog lost | Repository + Recovery Kit + recipient private material remain sufficient for recovery |
