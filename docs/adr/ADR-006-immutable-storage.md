# ADR-006: Immutable S3 Recovery Points & WORM Retention

- Status: **Accepted & Implemented in Code**
- Date: **September 3, 2026**
- Scope: Ransomware resilience, S3 Object Lock WORM semantics, and delete marker recovery

---

## Context

Client-side encryption prevents unauthorized disclosure of backup archives, but fails to prevent malicious deletion: an attacker with standard write access to an S3 bucket can wipe all objects without knowing the encryption key.

Real-world S3 testing uncovered that Object Lock protects object *versions*, not object *keys*. An attacker issuing unversioned `DELETE` calls inserts delete markers, rendering the repository apparently empty while versioned blobs survive.

---

## Decision

1. **WORM Storage Enforcement**: Fortiq supports S3 Object Lock in both Governance and Compliance modes.
2. **Pre-Provisioning Validation**: `RepositoryProvisioner` invokes `GetObjectLockConfiguration` before creating repositories; buckets without confirmed Object Lock retention cannot be designated as immutable.
3. **Automated Delete-Marker Recovery (`S3HiddenObjectRecovery`)**:
   - Fortiq actively scans object versions to detect keys where the latest version is a delete marker masking intact underlying data blobs.
   - Automatically removes malicious delete markers to restore full repository visibility while leaving engine lock objects unresurrected.
4. **Strict Permission Segregation**:
   - Endpoint backup tokens are granted write access, but are strictly denied `s3:DeleteObjectVersion` and `s3:BypassGovernanceRetention`.
   - Pruning and retention cleanup require dedicated maintenance credentials evaluated in separate windows.
