# Fortiq Architecture & Design Documentation

Documentation Suite Status: **Active Reference (v1.0-alpha)**  
Date: **September 2026**

## Implementation Status

Every specification carries an implementation status directly under its title. The values mean:

- **Implemented**: the described behaviour exists in `src/` and is covered by tests.
- **Partially implemented**: parts exist; the document marks inline which parts do not.
- **Design intent**: specified, not built. No implementation exists.
- **Strategy / Planning / Analysis**: the document is not a claim about code.

A specification written in the present indicative is describing a design, not reporting a fact, unless its status says otherwise. Statuses are re-checked against `src/` whenever a document is revised.

---

## Document Index

### 1. Fundamentals & Strategy
1. [01. Product Vision & Boundaries](01-product-vision.md)
2. [02. System Architecture](02-system-architecture.md)
3. [03. Threat Model & Trust Boundaries](03-threat-model.md)
4. [04. Key Management & Sovereign Access Recovery](04-key-management.md)
5. [05. Recovery Assurance](05-recovery-assurance.md)
6. [06. Fortiq Intelligence & Phi Silica (On-Device AI)](06-on-device-ai.md)
7. [07. Product Roadmap](07-roadmap.md)
8. [08. Open Decisions & Trade-offs](08-open-decisions.md)

### 2. Core Engine & Platform Services
9. [09. Repository Engine Contract](09-repository-engine-contract.md)
10. [10. Autonomous Disaster Recovery Sequence](10-disaster-recovery-sequence.md)
11. [11. Executable Prototype & Verification](11-executable-prototype.md)
12. [12. IPC Protocol & Security Profile](12-ipc-security-profile.md)
13. [13. Windows Capture: VSS & USN Journal](13-windows-capture.md)
14. [14. Storage Immutability & Ransomware Defense](14-storage-immutability.md)
15. [15. Audit, Evidence & Compliance Mapping](15-audit-compliance.md)

### 3. Operations, Fleet & User Experience
16. [16. Supply-Chain Security & Update Trust](16-supply-chain-updates.md)
17. [17. Product UX & Safe User Workflows](17-product-ux.md)
18. [18. Fleet & MSP Control Plane](18-fleet-control-plane.md)
19. [19. Reliability, Observability & SLO](19-reliability-observability.md)
20. [20. Local Catalog & Data Model](20-local-catalog-data-model.md)
21. [21. Embedded GUI Installer, System Discovery & Component Lifecycle](21-embedded-installer-and-updater.md)
22. [22. Desktop UI Architecture & Visual Design System](22-desktop-ui-and-visual-system.md)
23. [23. GUI Development Guidelines & Component Blueprint](23-gui-development-guidelines.md)

### 4. Architectural Decision Records (ADRs)
- [ADR-001: restic as Primary Engine for V1](adr/ADR-001-primary-repository-engine.md)
- [ADR-002: Recovery Envelope & Key Derivation](adr/ADR-002-recovery-envelope.md)
- [ADR-003: Process Boundaries for V1](adr/ADR-003-process-boundaries.md)
- [ADR-004: Windows Named Pipes for Local IPC](adr/ADR-004-windows-ipc.md)
- [ADR-005: VSS for Consistency, USN for Change Hints](adr/ADR-005-vss-usn.md)
- [ADR-006: Immutable S3 Recovery Points](adr/ADR-006-immutable-storage.md)
- [ADR-007: Tamper-Evident Audit Ledger](adr/ADR-007-audit-ledger.md)
- [ADR-008: TUF-Aligned Trust Model for Releases](adr/ADR-008-update-trust.md)
- [ADR-009: Recovery-First User Experience](adr/ADR-009-recovery-first-ux.md)
- [ADR-010: Metadata-Only Control Plane](adr/ADR-010-control-plane.md)
- [ADR-011: Evidence-Based Health Model](adr/ADR-011-reliability-model.md)
- [ADR-012: Recoverable Local Catalog](adr/ADR-012-local-catalog.md)
- [ADR-013: Argon2id Dependency & Cryptographic Supply-Chain Policy](adr/ADR-013-argon2-dependency-policy.md)
- [ADR-014: Embedded GUI Installer & Autonomous Component Updater](adr/ADR-014-embedded-gui-installer-and-updater.md)
- [ADR-015: Desktop UI Architecture, Visual Design System & Zero-State Lifecycle](adr/ADR-015-desktop-ui-architecture-and-design-system.md)

---

## Normative Terminology

- **MUST**: Mandatory requirement for the platform.
- **SHOULD**: Expected platform behavior; deviation requires an approved ADR.
- **MAY**: Optional feature or operational mode.

Security assertions MUST link to a verifiable requirement or automated test. Compliance assertions are formulated as supported technical safeguards rather than automated legal guarantees.
