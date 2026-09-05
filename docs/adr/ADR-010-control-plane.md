# ADR-010: Metadata-Only, Offline-First Control Plane

- Status: **Accepted Architecture**
- Date: **September 3, 2026**
- Scope: Fleet management, MSP multi-tenancy, and autonomous endpoint operations

---

## Context

Enterprise fleets and MSPs require unified health visibility, policy distribution, and compliance aggregation. However, if the control plane stores master encryption keys or becomes a mandatory runtime gateway for backups and restores, the platform forfeits sovereignty and creates a catastrophic single point of failure.

---

## Decision

1. **Metadata-Only Processing**: The control plane receives telemetry, aggregated health states, and audit receipts. Plaintext keys (EUS/RMK) and file payload contents never transit the control plane.
2. **Local Autonomous Execution**: Endpoints operate using locally cached, cryptographically-signed policies. Backups, integrity checks, and scheduled restore verifications continue during cloud or network outages.
3. **Outbound-Only mTLS**: Endpoints establish outbound connections to the control plane; no inbound management ports are opened.
4. **Typed Remote Commands**: Commands are strictly typed schema objects (e.g. `InitiateDrill`, `UpdatePolicy`). Arbitrary shell execution or remote code execution is architecturally impossible.
5. **Subscription Independence**: Emergency restores via `Fortiq.Recover` require zero license checks, network tokens, or cloud service reachability.
