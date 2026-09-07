# Specification 24: Community Resource Model

> **Implementation status: Design intent.**  
> This specification defines the target Community configuration/domain model. Existing Community builds remain repository/schedule-centric until the migration in Spec 27 is implemented.

## 1. Purpose

Fortiq Community MUST model reusable resources independently from backup execution policy.

The primary resource categories are:

1. Source
2. Storage
3. Storage Credential
4. Repository Engine
5. Identity
6. Identity Key
7. Encryption Profile
8. Backup Repository
9. Recovery Kit

A Backup Task composes these resources; it does not redefine them.

## 2. Source

A Source describes **what data Fortiq may read for protection**.

```json
{
  "id": "source-documents",
  "name": "Documents",
  "kind": "folder",
  "path": "C:\\Users\\Alice\\Documents"
}
```

Community v1 target kinds:

- `folder`
- `volume-filesystem`

Future kinds MAY include application-aware datasets, databases, VMs and block-image sources.

A `volume-filesystem` means filesystem-level backup of a volume. It MUST NOT be described as bare-metal/block imaging unless the selected engine/source adapter provides block semantics.

A Source MUST NOT contain destination storage, engine selection, storage credentials, recovery recipients or recurrence.

## 3. Storage

A Storage describes **a concrete place where repositories may be stored**.

```json
{
  "id": "storage-home-minio",
  "name": "Home MinIO",
  "backend": "s3",
  "endpoint": "https://minio.home.example",
  "bucket": "fortiq",
  "credentialRef": "cred-home-minio"
}
```

Backends may include:

- `filesystem`
- `s3`
- `sftp`

Provider/product labels such as MinIO, AWS S3, Wasabi or Hetzner are descriptive metadata and MUST NOT replace capability detection.

Storage capabilities SHOULD represent properties such as:

```text
Remote
Removable
Versioned
Immutable
ObjectLock
EncryptedTransport
IndependentOfEndpoint
CurrentlyAvailable
```

Capabilities that affect recovery/ransomware claims MUST be probed when possible.

## 4. Storage Credential

A Storage Credential grants transport/storage access.

Examples:

- S3 access key;
- SFTP private key or agent reference;
- SMB credential.

Storage Credentials MUST remain separate from encryption Identities.

> Storage access asks: **Can this process reach the backup bytes?**  
> Identity asks: **Can this principal decrypt the backup bytes?**

Secret material MUST NOT be embedded in Task JSON, repository metadata, logs or receipts.

## 5. Repository Engine

A Repository Engine implements a repository format and operations such as initialize, backup, list, check, restore, reconcile and retention.

Community v1 has one engine: `restic 0.19.1`.

The model MUST still represent the engine explicitly so repositories and Recovery Kits remain format-aware.

A dynamic plugin framework is not required by this specification.

## 6. Identity and Identity Key

An Identity represents a recovery or writer principal, not a Windows account and not a storage login.

Examples:

```text
Alice
Emergency Paper Recovery
This PC
Company Recovery Officer
Offline USB Key
```

An Identity MAY have multiple Identity Keys for rotation, custody separation or retirement.

Detailed cryptographic semantics are defined in Spec 26.

## 7. Encryption Profile

An Encryption Profile is a reusable access policy referenced by Routes.

```json
{
  "id": "enc-personal-recovery",
  "name": "Personal Recovery",
  "recipients": [
    "identity-key-alice-main",
    "identity-key-paper-recovery"
  ],
  "writers": [
    "identity-key-this-pc-tpm"
  ]
}
```

It separates:

- **Recipients** — principals able to recover/decrypt.
- **Writers** — principals/mechanisms able to unlock for unattended recurring writes.

## 8. Backup Repository

A Repository is a concrete encrypted archive instance materialized by:

```text
Storage + Engine + Encryption Profile + repository-specific identity
```

A repository owns/binds:

- Repository ID;
- engine format/version constraints;
- engine unlock secret;
- key envelopes;
- snapshots;
- Recovery Kit;
- repository-scoped evidence;
- repository run locks.

A Storage MAY contain multiple repositories.  
A Source MAY be protected into multiple repositories.

Repository MUST NOT be used as the user-facing synonym for Storage.

## 9. Recovery Kit

Recovery Kit remains repository-scoped.

It MUST carry enough open metadata to identify repository, engine requirements, storage locator information and envelope metadata.

It MUST NOT contain recipient private keys, mnemonic words or plaintext engine unlock secrets.

## 10. Canonical relationships

```text
Source
  │
  └──────────────┐
                 ▼
             Backup Task
                 │
                 └── Route
                     ├── Storage
                     ├── Engine
                     ├── Encryption Profile
                     └── Retention Policy
                              │
                              ▼
                         Repository
                              │
                              └── Recovery Kit
```

## 11. Community UX projection

The UI SHOULD primarily expose:

```text
Sources
Storage
Backup Tasks
Recovery
```

Engines, repository IDs, envelope IDs and low-level key suites SHOULD remain under Advanced/Diagnostics unless needed for recovery.

Prefer:

```text
Documents
Every day at 02:30
→ External SSD
→ Home MinIO
Recovery: Alice + Paper Recovery
```

over UUID-oriented repository rows.
