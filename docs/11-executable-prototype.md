# Executable Prototype & Verification Plan

> **Implementation status: Implemented.** Describes the test harness that exists.


## Objective

The executable prototype validates the complete, sovereign disaster recovery workflow under real conditions rather than demonstrating isolated mock APIs:

```text
source files
  → consistent source capture (VSS / NTFS)
  → client-side encrypted repository (restic)
  → complete deletion of local Fortiq state
  → unlock via recovery envelope / recovery kit
  → restore on a pristine machine via Fortiq.Recover
  → SHA-256 byte-level verification of restored content and metadata
```

The prototype milestone is considered successful exclusively upon passing test **DR-001 / E2E-001**. 

---

## Prototype Scope

### Implemented Capabilities
- **Platform**: Windows x64 (.NET 10 LTS);
- **Repository Backends**: Local directory backend and S3-compatible object storage;
- **Engine**: Pinned restic 0.19.1 with pre-execution SHA-256 binary validation and TOCTOU protection;
- **Key Envelopes**: `KeyEnvelopeV1` (RFC 8949 deterministic CBOR), `Bip39RecoveryEnvelopeV1` (English mnemonic wordlist), `WindowsTpmEnvelopeV1` (Microsoft Platform Crypto Provider), and `RecoverySecretEnvelope` (HKDF-SHA-256 + AES-256-GCM);
- **Core Operations**: `init`, `backup`, `snapshots`, `check`, `restore`, `prune`, `forget`, and `reconcile/unlock`;
- **Security Seam**: Isolated ephemeral Named Pipe broker (`Fortiq.PasswordHelper`) for `--password-command` execution;
- **Evidence Trail**: Cryptographic JSON operation receipts (`fortiq.operation-receipt`) recorded via `ReceiptRecordingBackupRepository`;
- **Recovery Tool**: Autonomous, zero-dependency CLI `Fortiq.Recover` reading recovery mnemonics strictly from standard input;
- **S3 Ransomware Recovery**: Automated recovery from malicious delete-marker tampering via `S3HiddenObjectRecovery`;
- **Scheduling & Service**: Unattended background scheduler worker (`Fortiq.Service`) evaluating cron and interval schedules;
- **Desktop UI**: Avalonia UI desktop application (`Fortiq.Desktop`) with Protection Setup Wizard and `ProveRecoveryAdapter`.

### Open Implementation Gates
- **Production Argon2id**: Vetting and selection of production-grade Argon2id implementation per ADR-013 review gates.

---

## Solution Project Structure

```text
Fortiq.sln
src/
  Fortiq.Domain/                 # Core domain entities, value objects, and policies
  Fortiq.Application/            # Port interfaces, use cases, and contracts
  Fortiq.Infrastructure.Keys/    # Key envelopes, BIP-39 codecs, TPM, memory zeroization
  Fortiq.Infrastructure.Restic/  # Pinned restic process adapter and JSONL parsers
  Fortiq.Infrastructure.ObjectStorage/ # S3 Object Lock and delete marker recovery
  Fortiq.Infrastructure.Receipts/     # Operation receipt store and audit ledger
  Fortiq.Infrastructure.Runs/         # Local run registry and atomic directory locks
  Fortiq.Platform.Windows/       # VSS snapshots, USN journal, Authenticode checks
  Fortiq.Provisioning/           # Transactional repository and recovery kit provisioning
  Fortiq.Scheduling/             # Schedule evaluation, recurrence, and missed windows
  Fortiq.Monitoring/             # Health metrics, recoverable SLA evaluation
  Fortiq.Operations/             # Proven restore, unattended backups, health publisher
  Fortiq.Service/                # Windows Service host worker
  Fortiq.Desktop/                # Avalonia UI application
  Fortiq.Desktop.ViewModels/     # MVVM presentation logic
  Fortiq.PasswordHelper/         # One-time password broker process
  Fortiq.Recover/                # Standalone emergency restore CLI
tests/
  Fortiq.Domain.Tests/           # Unit tests for domain models and value objects
  Fortiq.Restic.ContractTests/   # Contract tests against restic 0.19.1 CLI fixtures
  Fortiq.Security.Tests/         # Cryptographic envelope, unwrap, and zeroization tests
  Fortiq.Recovery.IntegrationTests/ # End-to-end integration tests (E2E-001..005)
  Fortiq.Scheduling.Tests/       # Recurrence and timezone transition tests
  Fortiq.Monitoring.Tests/       # Health evaluation and publication tests
  Fortiq.Desktop.Tests/          # ViewModel and UI adapter unit tests
test-assets/
  restic-output/0.19.1/          # Sanitized golden JSON/text event fixtures
  bip39/                         # Official BIP-39 test vectors
engines/
  manifest.json                  # Pinned binary metadata, lengths, and SHA-256 hashes
```

---

## End-to-End Test Vectors (E2E)

### E2E-001: Clean Machine Sovereign Restore (Passed)
- Dataset builder constructs an exhaustive test set (empty files, binary blobs, Unicode names, deep paths, read-only files).
- Backup executes into an isolated repository.
- Complete local Fortiq state and temporary files are deleted.
- Standalone `Fortiq.Recover` restores the dataset using only the recovery kit (`kit.json`) and mnemonic phrase fed via stdin.
- Restored files are verified against original SHA-256 checksums and file attributes.

### E2E-002: Incorrect Secret Rejection (Passed)
- Restore requested with invalid mnemonic passphrase.
- Process fails closed with uniform `UnlockFailedException` (exit code 77).
- Metadata, snapshot listings, and internal cryptographic parameters remain unrevealed.

### E2E-003: Corrupted Repository Detection (Passed)
- Random corruption introduced into repository data pack files.
- `check` identifies corrupted blobs and generates a failure receipt with detailed diagnostics.

### E2E-004: Interrupted Run Reconciliation (Passed)
- Backup process forcefully terminated mid-execution.
- Subsequent run acquires exclusive lock, invokes `unlock` reconciliation, and completes a valid snapshot.

### E2E-005: Path Traversal & Staging Boundary Enforcement (Passed)
- Dataset containing malicious junction points and symlinks targeted for restore.
- Verification confirms restore never writes outside the designated private staging directory.
- Atomic directory rename promotes verified files into target path.

---

## Implementation Progress Checklist

1. [x] Solution setup and Domain value objects.
2. [x] Engine manifest and pre-execution binary verification.
3. [x] Process runner and streaming JSON parsers.
4. [x] Local repository adapter with `IEngineCredentialProvider`.
5. [x] Ephemeral Named Pipe password helper and Authenticode client validation.
6. [x] Autonomous recovery CLI (`Fortiq.Recover`): `inspect`, `snapshots`, `check`, `restore`.
7. [x] Comprehensive E2E test suite (E2E-001 through E2E-005).
8. [x] Cryptographic operation receipts (`fortiq.operation-receipt`).
9. [x] S3-compatible backend integration with Object Lock verification.
10. [x] Ransomware delete-marker unmasking (`S3HiddenObjectRecovery`).
11. [x] Recurrence scheduling engine with DST and timezone handling.
12. [x] Unattended Windows Service (`Fortiq.Service`).
13. [x] Health observability (`health.json` and Prometheus metrics).
14. [x] Avalonia Desktop application (`Fortiq.Desktop`) with live recovery proof.
15. [ ] Production Argon2id dependency vetting per ADR-013.
