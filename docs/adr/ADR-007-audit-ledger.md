# ADR-007: Tamper-Evident Audit Ledger & Operation Receipts

- Status: **Accepted & Implemented in Code**
- Date: **September 3, 2026**
- Scope: Operation receipts, tamper-evident hash chaining, and compliance evidence

---

## Context

Standard flat log files are easily altered or deleted following an endpoint compromise. Plain digital signatures alone are also insufficient: an adversary can truncate the ledger tail or forge future events using a compromised active key.

Fortiq requires a verifiable audit ledger that mathematically detects historical tampering and truncation while never leaking plaintext file content or encryption keys.

---

## Decision

1. **Structured Operation Receipts (`fortiq.operation-receipt`)**: Implemented via `OperationReceipt.cs` (`Fortiq.Application`) and `FileSystemOperationReceiptStore.cs` (`Fortiq.Infrastructure.Receipts`). Every backup, check, prune, and restore emits a comprehensive receipt.
2. **Cryptographic SHA-256 Hash Chaining**: Receipts form a continuous cryptographic hash chain where each event incorporates the previous event's digest.
3. **Signed Checkpoints (`COSE_Sign1`)**: Segments are sealed using standard COSE single-signer structures.
4. **External Anchoring**: Checkpoints are periodically mirrored to independent immutable destinations (e.g. S3 Object Lock compliance storage or external SIEMs).
5. **Fail-Closed Policy for Destructive Actions**: While backup execution may continue in degraded logging states to preserve business availability, destructive operations (pruning, policy relaxation, key revocation) fail closed if ledger integrity is broken.
