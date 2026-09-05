# Product Vision & Boundaries

> **Implementation status: Strategy.** Product framing and V1 boundaries; makes no claim about what is built.


## Problem Statement

Small-and-medium businesses (SMBs) and regulated organizations frequently perform backups, yet lack reliable proof that they can restore mission-critical data within required Recovery Time Objectives (RTO) and Recovery Point Objectives (RPO). Compounding this risk are vendor lock-in, third-party key custody, ransomware attacks targeting backup stores, and untested disaster recovery procedures.

## Product Definition

Fortiq is a sovereign disaster recovery platform that:

1. Creates client-side encrypted, versioned backup repositories;
2. Protects backup history against destruction by compromised endpoints through immutable storage;
3. Continuously and automatically verifies actual restore capability;
4. Empowers customers to choose and combine key management methods (BIP-39 mnemonic phrases, TPM, KMS);
5. Preserves a completely autonomous, zero-dependency emergency restore path independent of Fortiq cloud services or account infrastructure.

## Target Audience

- SMBs with 5–250 endpoints lacking a dedicated backup engineering team;
- Managed Service Providers (MSPs) servicing multiple regulated clients;
- Legal, healthcare, architecture, financial, and auditing firms;
- Enterprises subject to strict data locality, sovereignty, and Customer-Managed Key (CMK) compliance mandates.

## Core Customer Outcomes

- **Recovery Confidence**: The administrator observes not merely the timestamp of the last backup run, but verifiable recovery confidence derived from real restoration tests.
- **Sovereign Recovery**: Data owners can fully restore archives following total hardware loss, active directory loss, or Fortiq cloud infrastructure unavailability.
- **Ransomware Defense**: A compromised endpoint cannot delete or corrupt immutable backup history held in compliant WORM storage.
- **Auditable Evidence**: Compliance auditors receive cryptographic, tamper-evident receipts for backups, restore verifications, and key management operations.
- **Local AI Guidance**: Users can formulate queries in natural language and receive safe restore assistance executed entirely on-device without telemetry or cloud data leakage.

## Version 1 Scope & Boundaries

### In Scope (V1)
- Windows file-system backup (NTFS, VSS snapshots, USN change hints);
- Local storage and S3-compatible object storage targets;
- Core restic engine integration with verified binary integrity;
- Hardware TPM, BIP-39 mnemonic recovery secrets, and key envelope wrapping;
- Immutable retention enforcement (S3 Object Lock);
- Autonomous recovery CLI (`Fortiq.Recover`) and desktop UI (`Fortiq.Desktop`).

### Out of Scope (V1)
- Bidirectional peer-to-peer workspace file sync;
- Proprietary deduplication chunk format;
- Bare-metal block-level disk imaging;
- Full KMIP enterprise client;
- Autonomous AI execution of destructive or unconfirmed actions.

## Key Product Metrics

- **Protected Endpoint Ratio**: Percentage of registered endpoints with successful backups within target RPO;
- **Recovery Assurance Ratio**: Percentage of active repositories with successful restore verification within target SLA;
- **Median Time to Restore (MTTR)**: Actual measured duration for sample dataset restoration;
- **Autonomous Recovery Gap**: Count of repositories lacking a validated, independent offline recovery kit (target: 0);
- **Policy Violations**: Incidence rate of unencrypted transfers or mutable storage assignments, and mean time to remediation.
