# Open Architecture Decisions & Resolution Log

> **Implementation status: Planning.** Trade-off register.


Each major architectural direction is formally evaluated and documented via an Architecture Decision Record (ADR).

---

## Architectural Decision Log

| ID | Topic | Current Status & Outcome | Reference |
| :--- | :--- | :--- | :--- |
| **DEC-001** | Primary Repository Engine | **Accepted & Implemented**: restic 0.19.1 selected for V1. | [ADR-001](adr/ADR-001-primary-repository-engine.md) |
| **DEC-002** | Licensing & Commercial Boundaries | Open / Community core with dual-licensing options. | Project Governance |
| **DEC-003** | Recovery Envelope & Key Derivation | **Accepted & Implemented**: RFC 8949 CBOR `KeyEnvelopeV1`, BIP-39, TPM. | [ADR-002](adr/ADR-002-recovery-envelope.md) |
| **DEC-004** | TPM Sealing & Device Identity | **Accepted & Implemented**: Microsoft Platform Crypto Provider (`WindowsTpmEnvelopeV1`). | [ADR-002](adr/ADR-002-recovery-envelope.md) |
| **DEC-005** | S3 Providers & Object Lock WORM | **Accepted & Implemented**: S3 Object Lock verification and delete marker unmasking. | [ADR-006](adr/ADR-006-immutable-storage.md) |
| **DEC-006** | Local IPC Transport & Security | **Accepted & Implemented**: Named Pipes with client PID matching and token checks. | [ADR-004](adr/ADR-004-windows-ipc.md) |
| **DEC-007** | Recovery Confidence Calculation | **Accepted & Implemented**: `Fortiq.Monitoring` evaluating actual restore proof. | [ADR-011](adr/ADR-011-reliability-model.md) |
| **DEC-008** | Windows App SDK / Phi Silica | Pending Windows Copilot+ PC runtime ecosystem maturity. | Spec 06 |
| **DEC-009** | Telemetry & Data Residency | **Accepted**: Zero cloud data telemetry; local file-based telemetry (`health.json`). | Spec 18, 19 |
| **DEC-010** | Supported OS Targets for V1 | **Accepted**: Windows 10/11 x64 and Windows Server 2022+ (.NET 10 LTS). | Spec 02, 13 |
| **DEC-011** | Windows Capture Coordination | **Partially implemented**: VSS snapshots (`--use-fs-snapshot`); USN hints remain design intent. | [ADR-005](adr/ADR-005-vss-usn.md) |
| **DEC-012** | Direct Locked Repo vs Mirror | **Accepted & Implemented**: Direct S3 Object Lock with `S3HiddenObjectRecovery`. | [ADR-006](adr/ADR-006-immutable-storage.md) |
| **DEC-013** | Audit Ledger & Receipts | **Partially implemented**: atomic `OperationReceipt` JSON records; no MAC, signature or hash chain. | [ADR-007](adr/ADR-007-audit-ledger.md) |
| **DEC-014** | Supply-Chain & Binary Trust | **Accepted & Implemented**: SHA-256 manifest pinning, handle-locking, CycloneDX SBOM. | [ADR-008](adr/ADR-008-update-trust.md) |
| **DEC-015** | Packaging & Distribution | **Accepted Architecture**: Embedded GUI installer & updater with portable fallback. | [ADR-014](adr/ADR-014-embedded-gui-installer-and-updater.md) |
| **DEC-016** | Recovery-First UX | **Accepted & Implemented**: Avalonia MVVM with mandatory mnemonic challenge. | [ADR-009](adr/ADR-009-recovery-first-ux.md) |
| **DEC-017** | Mnemonic Presentation Standards | **Accepted & Implemented**: Standard English BIP-39 2048-wordlist embedded in assembly. | [ADR-002](adr/ADR-002-recovery-envelope.md) |
| **DEC-018** | Fleet Control Plane Boundaries | **Accepted**: Metadata-only control plane architecture; zero customer data access. | [ADR-010](adr/ADR-010-control-plane.md) |
| **DEC-019** | Evidence-Driven Health Model | **Accepted & Implemented**: `HealthVerdict` distinguishing `Recoverable`, `Unproven`, and `AtRisk`. | [ADR-011](adr/ADR-011-reliability-model.md) |
| **DEC-020** | Local Run Registry & Storage | **Accepted & Implemented**: Recoverable filesystem JSON document store + OS kernel file handle locks. | [ADR-012](adr/ADR-012-local-catalog.md) |
| **DEC-021** | Argon2id Dependency Selection | Active Security Gate: Vetting standalone Argon2id per ADR-013 criteria. | [ADR-013](adr/ADR-013-argon2-dependency-policy.md) |
| **DEC-022** | Embedded GUI Installer & Updater | **Accepted Architecture**: Single-executable dual mode, system inspection, and TUF atomic updates. | [ADR-014](adr/ADR-014-embedded-gui-installer-and-updater.md) |
| **DEC-023** | Desktop UI Design System & Zero-State Lifecycle | **Accepted Architecture**: light Fluent v2 palette (Dark Slate superseded by ADR-015 Revision 1), native folder pickers, and resilient onboarding state machine. | [ADR-015](adr/ADR-015-desktop-ui-architecture-and-design-system.md) |
| **DEC-024** | Desktop Design Token Layer | **Accepted & Implemented**: a single C# token layer (`DesignTokens`) rather than XAML resource dictionaries. Runtime theme switching is out of scope until something requires it. | [Spec 23 §9](23-gui-development-guidelines.md), [ADR-015 Revision 2](adr/ADR-015-desktop-ui-architecture-and-design-system.md) |

---

## Approved Architectural Decision Records (ADRs)

1. [ADR-001: restic as Primary Engine for V1](adr/ADR-001-primary-repository-engine.md)
2. [ADR-002: Recovery Envelope & Key Derivation](adr/ADR-002-recovery-envelope.md)
3. [ADR-003: Process Boundaries for V1](adr/ADR-003-process-boundaries.md)
4. [ADR-004: Windows Named Pipes for Local IPC](adr/ADR-004-windows-ipc.md)
5. [ADR-005: VSS for Consistency, USN for Change Hints](adr/ADR-005-vss-usn.md)
6. [ADR-006: Immutable S3 Recovery Points](adr/ADR-006-immutable-storage.md)
7. [ADR-007: Tamper-Evident Audit Ledger](adr/ADR-007-audit-ledger.md)
8. [ADR-008: TUF-Aligned Trust Model for Releases](adr/ADR-008-update-trust.md)
9. [ADR-009: Recovery-First User Experience](adr/ADR-009-recovery-first-ux.md)
10. [ADR-010: Metadata-Only Control Plane](adr/ADR-010-control-plane.md)
11. [ADR-011: Evidence-Based Health Model](adr/ADR-011-reliability-model.md)
12. [ADR-012: Recoverable Local Catalog](adr/ADR-012-local-catalog.md)
13. [ADR-013: Argon2id Dependency & Cryptographic Supply-Chain Policy](adr/ADR-013-argon2-dependency-policy.md)
14. [ADR-014: Embedded GUI Installer & Autonomous Component Updater](adr/ADR-014-embedded-gui-installer-and-updater.md)
15. [ADR-015: Desktop UI Architecture, Visual Design System & Zero-State Lifecycle](adr/ADR-015-desktop-ui-architecture-and-design-system.md)
