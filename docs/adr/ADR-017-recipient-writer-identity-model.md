# ADR-017: Separate Recovery Recipients, Unattended Writers and Storage Credentials

- Status: **Proposed**
- Date: **September 7, 2026**
- Scope: Community cryptographic authority model

## Context

Current Fortiq supports several repository unlock envelopes around one EUS, including TPM and BIP-39.

Community needs a reusable Identity model, including future ability to configure public recovery recipient keys while keeping private recovery material offline.

Three authorities must be distinguished:

1. decrypt/recover repository data;
2. unlock an existing repository for unattended recurring writes;
3. authenticate to storage transport.

## Decision

Model these separately:

```text
Recipient Identity Key
Writer Identity/Unlock
Storage Credential
```

An Encryption Profile binds Recipients and Writers.

### Recipient

Receives an envelope of repository EUS. A public recipient key may be stored in configuration.

### Writer

Can reopen the repository for future backups. For current restic recurring operation, a public recipient key alone is insufficient.

### Storage Credential

Authorizes access to S3/SFTP/filesystem transport and grants no plaintext decryption authority.

## V1 compatibility constraint

With restic:

> one repository represents one repository-wide encryption/access policy.

Per-snapshot recipient ACLs are not claimed.

Routes requiring different recipient policies should materialize distinct repositories.

## Cryptographic construction

This ADR establishes roles, not a final asymmetric algorithm.

Fortiq MUST use a standard reviewed public-key envelope construction and publish stable test vectors before claiming implementation.

Mnemonic-derived asymmetric identity requires a separate accepted cryptographic profile or revision defining deterministic, versioned, domain-separated derivation.

## Consequences

- backup endpoints may know recipient public keys without holding recipient private keys;
- one-shot archival mode may discard writer authority after completion;
- recurring backup still requires writer unlock;
- TPM becomes a local unattended writer rather than the sole sovereign recovery identity;
- storage credential compromise remains distinct from decryption-key compromise.
