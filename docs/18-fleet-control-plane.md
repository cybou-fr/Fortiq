# Fleet & MSP Control Plane Architecture

> **Implementation status: Design intent.** No implementation.


## Purpose & Scope

The centralized Fleet & MSP Control Plane enables organizations to manage endpoint backup fleets, distribute verified protection policies, and aggregate recovery health telemetry without ever obtaining access to:
- Backup archive contents or filenames;
- Engine Unlock Secrets (EUS) or Key Encryption Keys (KEK);
- Customer BIP-39 recovery mnemonics;
- Customer KMS access credentials.

Endpoints operate autonomously: backups and scheduled restore verifications continue uninterrupted during temporary network partitions or cloud outages. Emergency disaster recovery is completely decoupled from subscription status or control-plane availability.

---

## Zero-Trust Cryptographic Boundaries

```text
┌────────────────────────────────────────────────────────┐
│               MSP & Enterprise Dashboard               │
└───────────────────────────┬────────────────────────────┘
                            │ Authenticated OIDC / MFA
┌───────────────────────────▼────────────────────────────┐
│           Fortiq Control Plane (Metadata Only)         │
│     Fleet health, policy signing, compliance reporting  │
└───────────────────────────┬────────────────────────────┘
                            │ Outbound mTLS Telemetry
                            │ (Signed receipts, health.json)
┌───────────────────────────▼────────────────────────────┐
│                  Endpoint Agent / Service              │
│       Execution, VSS capture, local TPM envelope       │
└───────────────────────────┬────────────────────────────┘
                            │ Direct Encrypted Backup
┌───────────────────────────▼────────────────────────────┐
│        Customer Sovereign Storage (Local / S3 WORM)    │
└────────────────────────────────────────────────────────┘

Data payloads NEVER transit through the Fortiq control plane.
```

### Explicit Control-Plane Non-Capabilities
The control plane is mathematically and architecturally incapable of:
1. Deriving, requesting, or unwrapping repository master keys;
2. Reading user file names, directories, or blob contents;
3. Executing arbitrary remote shell commands on endpoints;
4. Truncating or overriding S3 Object Lock retention in Compliance mode;
5. Disabling offline restoration using `Fortiq.Recover`.

---

## Multi-Tenant Fleet Organization

- **MSP Tenant Boundaries**: Strict cryptographic tenant isolation ensuring no cross-tenant metadata visibility.
- **Role-Based Access Control (RBAC)**: Support for Operators, Security Auditors, and Policy Administrators.
- **Signed Policy Distribution**: Endpoints only accept protection policies bearing valid cryptographic signatures from the tenant's authorized administrative keys.
