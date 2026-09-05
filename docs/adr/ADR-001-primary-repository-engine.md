# ADR-001: restic as Primary Repository Engine for V1

- Status: **Accepted & Implemented for V1**
- Date: **September 3, 2026**
- Scope: Backup repository format, content deduplication, restore, and integrity verification
- Re-evaluation: Following V1 GA milestones

---

## Context

Fortiq requires a single, reliable storage engine for Windows file backups. Priorities for V1:
1. Sovereign, autonomous restoration independent of Fortiq cloud infrastructure;
2. Mature, thoroughly audited, and open repository specification;
3. Robust command-line automation and process control;
4. Strong cryptographic data integrity verification;
5. Native support for local filesystems and S3-compatible cloud storage;
6. Permissive licensing (BSD-2-Clause) permitting unhindered distribution;
7. Minimal operational overhead.

Building a proprietary deduplication engine was explicitly rejected due to astronomical engineering and cryptographic verification costs. Comparing restic and Kopia, restic scored 158 vs 142 on weighted criteria, particularly excelling in operational simplicity and autonomous single-binary recovery.

---

## Decision

Adopt **restic (pinned version 0.19.1) as the sole primary storage engine for Fortiq V1**.

Fortiq:
- Distributes a verified, SHA-256 pinned restic binary;
- Orchestrates backup execution via a strongly-typed process adapter (`Fortiq.Infrastructure.Restic`);
- Delivers credentials strictly via an ephemeral, one-shot `--password-command` Named Pipe broker (`Fortiq.PasswordHelper`);
- Backs up directly from point-in-time VSS snapshots (`--use-fs-snapshot`);
- Invokes regular cryptographic integrity checks (`check`);
- Restores files into a sandboxed staging directory (`RestoreStagingArea`);
- Retains complete engine-independent recovery kits (`kit.json`).

---

## Key Management Implications

Restic generates an internal cryptographic master key protected by password-derived key entries. Fortiq treats restic's password as a high-entropy 256-bit **Engine Unlock Secret (EUS)**:

```text
TPM / BIP-39 / KMS Envelope (KeyEnvelopeV1)
              ↓ unwrap
     Engine Unlock Secret (EUS)
              ↓ Base64UrlNoPadding(EUS) via --password-command
 restic key entry → restic internal master key → repository data
```

---

## Consequences

- **JSON Parsing**: Where restic outputs non-JSON lines, Fortiq employs hardened streaming event parsers (`ResticJsonParser`) with golden fixtures.
- **Process Isolation**: The engine executes within a strictly stripped environment, using private `TEMP` locations.
- **Immutability Nuances**: Restic's interaction with S3 Object Lock is managed via `S3HiddenObjectRecovery` to handle delete markers.
