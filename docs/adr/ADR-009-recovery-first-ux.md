# ADR-009: Recovery-First User Experience

- Status: **Accepted & Implemented**
- Date: **September 3, 2026**
- Scope: Desktop application information architecture, onboarding, and status models

---

## Context

Conventional backup applications focus almost exclusively on setting up schedules and flashing green `Backup Succeeded` badges. This instills dangerous, unearned confidence: writing data blocks proves neither that the encryption keys are retained, nor that files can actually be restored on a fresh machine.

---

## Decision

Design Fortiq's user experience around **Recovery First**:
1. **Primary Screen Metric**: Highlights **Recovery Confidence** and the timestamp of the last verified restoration test rather than backup completion times.
2. **Distinct Unlock Roles**: Clarifies the difference between daily background device unlock (TPM) and sovereign disaster recovery (BIP-39 mnemonic phrase).
3. **Mandatory Onboarding Verification**: Onboarding does not finish until the operator verifies mnemonic retention via randomized challenge words and executes a live sample restore test.
4. **Non-Destructive Staging Default**: Restores default to private staging areas; in-place overwrites require multi-step approval.
5. **Conservative Status Categorization**: Repositories are labeled `Unproven` until actual restoration is validated.

---

## Consequences

- Onboarding requires more deliberate effort than a zero-thought wizard.
- The UI avoids misleading green checkmarks when restore capability remains unverified.
- Users gain tangible proof that their sovereign emergency recovery path functions properly.
