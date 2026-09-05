# Cryptographic Audit Ledger, Evidence & Compliance Mapping

> **Implementation status: Partially implemented.** Operation receipts exist. Compliance mappings state supported safeguards, not automated guarantees; read the caveats in each row.


## Purpose & Scope

Fortiq generates verifiable, technical evidence documenting backups, restorations, cryptographic key lifecycle events, retention policy enforcements, and administrative actions. These audit records assist organizations in fulfilling regulatory compliance requirements (e.g. EU GDPR, NIS2, ISO 27001), but do not constitute automated legal certification.

Compliance remains a shared responsibility depending on data controller policies, processing grounds, access controls, staff training, and incident response procedures.

---

## Core Audit Trail Properties

- **Completeness**: All security-relevant operations pass through the mandatory audit receipt pipeline (`IOperationReceiptStore`).
- **Tamper Evidence**: Any modification, insertion, deletion, or reordering of logged receipts is mathematically detectable via SHA-256 hash chains.
- **Truncation Detection**: Truncation or rollback of the ledger tail is exposed through comparison against external anchors.
- **Actor Attribution**: Operations are bound to verified Windows Service/User SIDs.
- **Data Minimization**: File contents, plaintext secrets, and unredacted customer data are strictly excluded from receipts.
- **Sovereign Portability**: Evidence packs can be verified offline using zero-dependency CLI tooling without connecting to a central control plane.

---

## Operation Receipt Schema (`fortiq.operation-receipt`)

Implemented via `OperationReceipt.cs` (`Fortiq.Application`) and stored atomically via `FileSystemOperationReceiptStore.cs` (`Fortiq.Infrastructure.Receipts`):

```json
{
  "schema": "fortiq.operation-receipt",
  "version": 1,
  "operationId": "c4b78a91-d2e3-4f5a-8b1c-9e2d3f4a5b6c",
  "operation": "backup",
  "repositoryId": "7c8e2a1b-4d9f-41f8-9a3b-6e2c1d0e8f4a",
  "engine": {
    "name": "restic",
    "version": "0.19.1",
    "sha256": "b0dd1fd21eea5d8fe1325f55f7118213c21f36de8a261e04c0624a5ab9fd7830"
  },
  "startedAt": "2026-09-04T10:00:00Z",
  "completedAt": "2026-09-04T10:04:12Z",
  "engineResult": "succeeded",
  "snapshotId": "3f8a9b2c",
  "source": {
    "kind": "filesystem",
    "stableId": "docs-drive-c",
    "consistency": "fileSystemSnapshot"
  },
  "metrics": {
    "bytesProcessed": 104857600,
    "filesProcessed": 1420
  },
  "warnings": []
}
```

---

## Strict Privacy & Data Exclusions

The audit ledger **MUST NEVER** record:
- Plaintext Engine Unlock Secrets (EUS), Key Encryption Keys (KEK), or recovery passphrases;
- Output from `--password-command` processes;
- Raw document content, payloads, or unredacted user file listings;
- Raw generative AI prompts or contextual fragments;
- Cloud storage secret keys, session tokens, or pre-signed URLs;
- Unsanitized stack traces containing memory buffer contents.

---

## Cryptographic Hash Chaining

```text
eventHash[0] = SHA-256(domainSeparator || ledgerHeader || event[0])
eventHash[n] = SHA-256(domainSeparator || eventHash[n-1] || event[n])
```

- Each ledger segment is sealed with a signed checkpoint referencing the previous segment's root hash.
- Independent anchors are published to external immutable targets (e.g. S3 Object Lock compliance buckets, customer SIEMs, or local WORM media).

---

## Regulatory Compliance Evidence Mapping

| Mandate / Regulation | Fortiq Technical Evidence Safeguard | Customer Residual Responsibility |
| :--- | :--- | :--- |
| **GDPR Art. 32(1)(a)**: Encryption of personal data | AES-256 repository encryption, `KeyEnvelopeV1` wrapping, memory zeroization. | Lawful processing grounds, key custody policies. |
| **GDPR Art. 32(1)(b)**: Ongoing resilience & integrity | Cryptographic check receipts, immutable S3 Object Lock, uncorrupted delete-marker recovery. | General network boundaries and infrastructure resilience. |
| **GDPR Art. 32(1)(c)**: Timely restoration of availability | Restore drills that restore the newest snapshot and reconcile restored bytes against the engine's own count (`ProvenRestore`), recorded as restore receipts. Drills run on a per-repository recurrence as well as on demand. Restore durations are not measured. | Validating business RTO/RPO requirements, measuring against them, and choosing a drill frequency. |
| **GDPR Art. 32(1)(d)**: Regular testing of effectiveness | Scheduled restore verifications, verifiable recovery status reporting. | Reviewing test findings and scheduling regular recovery drills. |
| **NIS2 Art. 21(2)(c)**: Business continuity & disaster recovery | Autonomous clean-machine restore CLI (`Fortiq.Recover`), self-contained kits (`kit.json`). | Formal corporate Business Continuity Plans (BCP) and crisis management. |
| **NIS2 Art. 21(2)(d)**: Supply-chain security | SHA-256 pinned engine manifest, TOCTOU handle locking, CycloneDX SBOM generation. | Third-party vendor risk assessment. |
| **NIS2 Art. 21(2)(h)**: Cryptographic policies | Versioned envelope suites, strict domain separation, memory-hard KDFs. | Formal corporate cryptographic key policy adoption. |
