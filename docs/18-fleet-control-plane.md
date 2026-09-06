# Fleet & MSP Control Plane Architecture

> **Implementation status: Design intent.** No implementation in this repository's default branch.
>
> A prototype of the cryptographic half of this design — canonical JSON, P-256 signed envelopes, the
> policy and telemetry schemas, and a server-side ingestion engine — was written and is kept on the
> `fleet-control-plane` branch. It was removed from `main` because nothing shipped it and nothing
> could: it had no transport, no persistence, no server and no interface, and it was never referenced
> by the desktop or the service. What it cost, it cost on every build and every change to the
> monitoring types it read.
>
> It was also the one thing in this repository that could reasonably worry somebody who came for the
> promise that Fortiq talks to no server of ours. A folder named `ControlPlane`, containing a class
> named `FleetTelemetryClient`, is a fair thing to be alarmed by — and "it never sends anything"
> is a weaker answer than not being there.
>
> Fleet management is a different product from the one this repository builds: it is for somebody
> administering many machines, and a person protecting their own PC needs no central anything. If it
> is built, it belongs beside Fortiq with its own promises rather than inside a community edition
> whose promise is that there is no server.


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
