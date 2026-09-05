# System Architecture

> **Implementation status: Partially implemented.** Component topology reflects the code. The Settings view
> exists; the Component Hub and Audit Receipts views described for the desktop shell do not.


## Architecture Context & Topology

```text
┌──────────────────────── Fortiq Desktop ────────────────────────┐
│ UI, configuration, recovery workflows, Avalonia MVVM           │
│ (src/Fortiq.Desktop, src/Fortiq.Desktop.ViewModels)            │
└──────────────────────────────┬───────────────────────────────────┘
                               │ shared schedules, receipts and health files
┌──────────────────────────────▼───────────────────────────────────┐
│ Fortiq Service (src/Fortiq.Service, src/Fortiq.Operations)       │
│ scheduler, health publisher, unattended jobs, audit logging      │
└──────────────┬───────────────────┬───────────────────┬────────────┘
               │                   │                   │
       ┌───────▼────────┐  ┌──────▼──────────┐  ┌─────▼──────────┐
       │ Windows Broker  │  │ Repository      │  │ Key Manager    │
       │ & Platform      │  │ Engine Adapter  │  │ & Envelopes    │
       │ (Platform.Win)  │  │ (Infr.Restic)   │  │ (Infr.Keys)    │
       └────────────────┘  └──────┬──────────┘  └─────┬──────────┘
                                  │                   │
                         ┌────────▼────────┐  ┌───────▼───────────┐
                         │ Local / S3 WORM │  │ TPM / BIP-39 /    │
                         │ (Infr.ObjStore) │  │ Recovery Kits     │
                         └─────────────────┘  └───────────────────┘

Autonomous Emergency Restore Path (zero runtime dependencies):
  src/Fortiq.Recover (CLI) + engines/ + kit.json → Restored Dataset
```

## Solution Components & Project Mapping (17 Source Projects)

Desktop and Service both compose the Operations layer and can launch engine work directly. The
vertical connection above denotes shared file state, not a Desktop-to-Service command pipe.
Named pipes currently carry engine passwords to the approved helper. A service command API remains
design intent.

### 1. Domain & Application Core (`src/Fortiq.Domain`, `src/Fortiq.Application`)
- **`Fortiq.Domain`**: Foundational domain primitives (`RepositoryId`, `RepositoryDescriptor`, `SnapshotDescriptor`, `BackupReceipt`, `CheckReceipt`, `RestoreReceipt`, `RetentionReceipt`, `StorageProtection`).
- **`Fortiq.Application`**: Engine contracts (`IRepositoryEngine`, `IBackupRepository`, `IRepositoryIdentityReader`), operation commands (`IOperationCommand`, `CreateSnapshot`, `RestoreSnapshot`, `ApplyRetention`), credentials (`IEngineCredentialProvider`, `IKeyLease`), and receipts (`OperationReceipt`).

### 2. Fortiq Desktop (`src/Fortiq.Desktop`, `src/Fortiq.Desktop.ViewModels`)
Cross-platform Avalonia UI application built upon clean MVVM separation.
- **Visual Design System & App Shell**: Windows 11 Fluent v2 styling, light surface hierarchy (`#F6F8FB` canvas, `#FFFFFF` cards), multi-view navigation rail (Home, Protect, Backups, Recovery, Settings), and embedded multi-resolution window/taskbar icon pipeline (`assets/icon.ico`, `Spec 22`, `ADR-015 Revision 1`). Settings is currently a placeholder view.
- **Zero-State Lifecycle & Resilient Onboarding**: State-driven health reader eliminating false-alarm startup errors on fresh installations, presenting a reassuring onboarding dashboard with system readiness checks.
- **Native Path Pickers**: Windows folder dialogs use Avalonia StorageProvider directly. Picker abstractions and extended storage guidance remain design intent.
- **Embedded Installer & System Discovery — design intent**: installation inspection, guided installation and component updates are specified but not implemented (`Spec 21`, `ADR-014`).
- **Least-Privilege Setup — design intent**: dedicated Service SID registration and installer-wide directory DACLs are not provisioned. Credential-store writes apply their own owner/SYSTEM/Administrators ACL.
- **Zero Key Custody in UI**: Never holds long-lived master encryption keys.
- **Evidence-Driven Status**: Renders system state derived strictly from verifiable evidence (`health.json`).
- **Protection Setup Wizard**: 5-step guided wizard featuring a 4x6 mnemonic card grid and mandatory slot-based challenge verification.
- **Live Recovery Proof**: Incorporates `ProveRecoveryAdapter` to execute live restore verifications directly from the user interface.

### 3. Fortiq Service & Operations (`src/Fortiq.Service`, `src/Fortiq.Operations`, `src/Fortiq.Scheduling`)
- **`Fortiq.Service`**: Unattended Windows Service host (`SchedulerWorker`); the deployment chooses its account. The repository does not install or configure that account.
- **`Fortiq.Operations`**: High-level operational workflows (`UnattendedBackup`, `ProvenRestore`) executing end-to-end backup, verification, and receipt logging.
- **`Fortiq.Scheduling`**: Wall-clock recurrence engine (`BackupSchedule`), timezone transitions, missed-run coalescing, and filesystem schedule storage (`FileSystemScheduleStore`).

### 4. Repository Provisioning (`src/Fortiq.Provisioning`)
- Transactional repository creation (`RepositoryProvisioner`) enforcing the invariant that an initialized repository cannot survive without a verified recovery kit (`kit.json`).
- Rollback safety on process interruption via intent recording (`provisioning-intent.json`).

### 5. Health & Observability (`src/Fortiq.Monitoring`)
- Pure fact-based evaluation (`HealthAssessor`) classifying repositories into `HealthVerdict` (`Recoverable`, `Unproven`, `AtRisk`).
- File-based telemetry publisher (`HealthPublication`) generating `health.json` (schema `fortiq.health-report` v1) and Prometheus text metrics (`fortiq.prom`).

### 6. Privileged Windows Platform & Security Broker (`src/Fortiq.Platform.Windows`, `src/Fortiq.PasswordHelper`)
- **`Fortiq.Platform.Windows`**: process handle pinning (`PinnedFile`), Authenticode verification and machine credential protection. The restic adapter requests VSS through `--use-fs-snapshot`; USN hints are not implemented.
- **`Fortiq.PasswordHelper`**: Ephemeral Named Pipe security broker (`\\.\pipe\fortiq-password-v1-{id:N}`) serving credentials via a 32-byte challenge-response handshake with PID, image, and account SID verification.

### 7. Engine & Storage Adapters (`src/Fortiq.Infrastructure.Restic`, `src/Fortiq.Infrastructure.ObjectStorage`)
- **`Fortiq.Infrastructure.Restic`**: Adapts restic behind `IRepositoryEngine`. SHA-256 pre-execution binary validation, handle-locking (`FileShare.Read`) to prevent TOCTOU, and JSONL streaming event parsers.
- **`Fortiq.Infrastructure.ObjectStorage`**: Verifies S3 Object Lock configuration (`S3StorageProtectionInspector`) and detects/unmasks malicious delete markers (`S3HiddenObjectRecovery`).

### 8. Keys, Receipts & Concurrency Control (`src/Fortiq.Infrastructure.Keys`, `src/Fortiq.Infrastructure.Receipts`, `src/Fortiq.Infrastructure.Runs`)
- **`Fortiq.Infrastructure.Keys`**: Deterministic CBOR `KeyEnvelopeV1` formats (`Bip39RecoveryEnvelope`, `WindowsTpmEnvelope`, `RecoverySecretEnvelope`), recovery kits (`RecoveryKit`), and zeroized memory buffers (`BufferKeyLease`).
- **`Fortiq.Infrastructure.Receipts`**: Atomic filesystem storage for audit receipts (`FileSystemOperationReceiptStore`).
- **`Fortiq.Infrastructure.Runs`**: OS-enforced file handle locking (`FileSystemRepositoryRunRegistry`) ensuring instant crash recovery via kernel handle cleanup.

### 9. Autonomous Emergency Recovery CLI (`src/Fortiq.Recover`)
A Windows x64 CLI independent of the Fortiq service, desktop UI and cloud:
- Requires the recovery distribution and helper, matching pinned engine, repository, kit and mnemonic; remote storage also requires network access and independent storage credentials.
- Reads recovery secrets from stdin to keep them out of command-line arguments. This does not protect against process memory inspection by a privileged attacker.

---

## Architectural Invariants

1. **No AI in Critical Paths**: Generative AI models and LLMs are strictly prohibited from participating in backup execution, key derivation, or restore pipelines.
2. **Catalog Crash Invariance**: Complete loss or corruption of the local catalog never compromises repository recoverability.
3. **Compromised Endpoint Confinement**: Endpoint credentials are mathematically incapable of deleting or corrupting immutable repository blocks protected by S3 Object Lock.
4. **Policy Enforcement on Destructive Operations**: Retention policies (`prune` / `forget`) require dry-run validation and exclusive repository leases (`Fortiq.Infrastructure.Runs`).
5. **Strict Envelope Versioning**: All envelope formats and receipt schemas carry explicit version identifiers and reject unrecognized fields.
6. **Platform Abstraction**: Windows-specific features (VSS, USN, Authenticode, Named Pipes) remain strictly encapsulated behind portable interfaces.
