# Product Roadmap

> **Implementation status: Planning.** Milestone tracker. Checkboxes are the claim; each is verified
> against `src/` when this document is revised.
>
> Every remaining item is work this project can execute on its own. Milestones that depended on a
> purchase or an outside party — a commissioned cryptographic review, a code-signing certificate, a
> penetration-testing engagement — were removed or replaced with an achievable equivalent, because a
> milestone nobody here can start is not a plan, it is an excuse with a checkbox.


The Fortiq roadmap is organized around risk reduction and verification milestones rather than an exhaustive feature inventory.

---

## Phase 0 — Core Architecture & Engine Validation (Completed)

- [x] Primary repository engine selection and verification (restic 0.19.1 pinned binary);
- [x] Pre-execution binary validation and TOCTOU handle-locking mitigation;
- [x] E2E-001 clean machine recovery test (backup → delete state → autonomous restore);
- [x] Threat model and key hierarchy formalization (`KeyEnvelopeV1`, RFC 8949 CBOR);
- [x] Zero-dependency emergency recovery CLI (`Fortiq.Recover`);
- [x] Memory zeroization and key leases (`BufferKeyLease`);
- [x] Ephemeral named pipe password broker (`Fortiq.PasswordHelper`).

**Exit Criteria Met**: A test dataset is backed up, local Fortiq state is wiped, and `Fortiq.Recover` restores the dataset using only the recovery kit, matching byte-for-byte SHA-256 hashes.

---

## Phase 1 — Sovereign Backup & Local Platform (Completed / In Progress)

- [x] **Desktop Application**: Cross-platform Avalonia UI desktop application (`Fortiq.Desktop`) with MVVM architecture (`Fortiq.Desktop.ViewModels`);
- [x] **Windows Service**: Unattended background execution host (`Fortiq.Service`) authenticated via device TPM;
- [x] **Windows Capture**: Volume Shadow Copy Service (VSS) snapshot support (`--use-fs-snapshot`);
- [ ] **USN Change Journal hints**: specified, no journal reader exists;
- [x] **Storage Targets**: Local directory storage and remote S3-compatible endpoints (`s3:https://...`);
- [x] **Envelope Suites**: Hardware TPM unlock (`WindowsTpmEnvelopeV1`) and BIP-39 mnemonic recovery (`Bip39RecoveryEnvelopeV1`);
- [x] **Immutability & Retention**: S3 Object Lock compliance verification re-checked at runtime rather than remembered from provisioning, retention pruning policies (`keep-daily`, `keep-weekly`) that take the repository exclusively in either mode and now run on a per-repository schedule (`ScheduledRetentionRunner`), and ransomware delete-marker recovery (`S3HiddenObjectRecovery`);
- [x] **Atomic Operation Evidence**: Structured operation receipts (`fortiq.operation-receipt`, schema v2) recorded for every operation, written atomically and chained by SHA-256 digest (`PreviousReceiptHash`, `ReceiptHash`). `AuditLedgerVerifier` enforces chain continuity against the repository-scoped `.ledger`, and heads are anchored outside the receipt directory via `IAuditLedgerAnchor` (ADR-007 implemented).
- [x] **Autonomous Recovery Tool**: Fully functional `Fortiq.Recover` CLI (`inspect`, `snapshots`, `check`, `restore`);
- [x] **Embedded GUI Installer & System Discovery**: prerequisite discovery (`InstallationInspector`), the setup wizard (`InstallWindow`), elevated install with restrictive DACLs, service registration (`WindowsServiceController`), bundle validation and a headless CLI (`InstallationCli`) (Spec 21, ADR-014). *The updater half of Spec 21 is not built — see Phase 1.5.*
- [x] **Desktop UI Redesign & Visual System**: Light Fluent v2 styling, native folder picker dialogs, 4x6 mnemonic card grid, and error-free zero-state onboarding (Spec 22, ADR-015 Revision 1);
- [x] **Per-Repository Storage Credentials**: object storage keys held per repository and encrypted to the machine (`StoredObjectStorageCredentials`). Writes restrict the credential directory to its owner, SYSTEM and administrators; reads reject unsafe legacy ACLs. Custom service identities and installer-wide permissions remain a deployment gate.
- [ ] **Production Password Envelope**: Argon2id dependency vetting against the five gates of [ADR-013 Revision 1](adr/ADR-013-argon2-dependency-policy.md) — RFC 9106 vectors, negative boundaries, x64/ARM64 packaging, SBOM and hash locking, and the adversarial agent audit with its evidence committed to `docs/audits/`.

---

## Phase 1.5 — Continuous Recovery Assurance & Observability (Current Active Focus)

- [x] **Verifiable Recovery Status**: Distinction between proven restores, unproven repositories, and at-risk backups (`Fortiq.Monitoring`);
- [x] **Live Restore Verification**: In-app restoration proof workflow via `ProveRecoveryAdapter`;
- [x] **Local Telemetry Publication**: Decoupled evidence reporting via `health.json` and Prometheus textfile (`fortiq.prom`);
- [ ] **In-App Component & Third-Party Engine Updater**: Transactional, rollback-safe self-updates for desktop, background service, and pinned restic engines under TUF with project-held offline root keys ([ADR-008 Revision 1](adr/ADR-008-update-trust.md), ADR-014). Authenticode signatures are verified when present and recorded when absent; no certificate purchase gates this milestone;
- [x] **Automated Scheduled Restore Drills**: opt-in `drillRecurrence` per schedule, restoring the newest snapshot into a disposable directory and recording the proof as a receipt (`ScheduledDrillRunner`, `UnattendedRestoreDrill`);
- [x] **Backup Anomaly Observation**: deduplication collapse and added-data/changed-file spikes measured against each repository's own median (`BackupAnomalyDetector`), surfaced as findings that never alter the recoverability verdict;
- [ ] **Audit Receipts & History Inspector**: a desktop view over the receipt chain that `AuditLedgerVerifier` already validates, so the ledger's integrity can be shown to somebody rather than only computed (Spec 17 section 7);
- [ ] **Pristine-machine recovery drill in CI**: DR-001 executed on a fresh `windows-latest` runner against published release artifacts, closing the acceptance gate in [Spec 10](10-disaster-recovery-sequence.md);
- [ ] Multi-repository fleet health aggregator for local workgroups and small networks.

---

## Phase 2 — Enterprise Custody & Key Infrastructure

- [ ] External KMS integration (HashiCorp Vault Transit engine, AWS KMS, Azure Key Vault);
- [ ] Multi-tenant OIDC, mTLS, and AppRole authentication profiles;
- [ ] Multi-envelope cryptographic key rotation;
- [ ] Multi-party authorization workflows for destructive retention policies;
- [ ] Centralized sovereign policy distribution.

---

## Phase 2.5 — Fortiq Intelligence (On-Device AI)

> Gated on hardware, not on effort: Phi Silica needs a Copilot+ PC with an NPU, and the project has
> none. Capability discovery and the absent-provider path are testable anywhere and can proceed; the
> inference items below stay unscheduled until such a machine exists. See [Spec 06](06-on-device-ai.md).

- [ ] Microsoft Phi Silica capability discovery and graceful absent-provider behaviour (buildable and testable without NPU hardware);
- [ ] Natural-language local log explanation and restore assistance;
- [ ] Natural-language recovery plan generation (strictly advisory; human-in-the-loop confirmation required);
- [ ] Strict metadata-only privacy boundaries (payloads never processed by AI);
- [ ] Adversarial prompt-injection test harness and guardrail validation.

---

## Phase 3+ — Advanced Fleet & Sovereign Ecosystem

- [ ] Multi-tenant MSP management portal;
- [ ] Enterprise KMIP 2.x protocol support;
- [ ] Bare-metal system imaging and recovery;
- [ ] Peer-to-peer sovereign data synchronization (`Fortiq Vault`).
