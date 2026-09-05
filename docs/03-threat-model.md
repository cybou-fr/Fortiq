# Threat Model & Trust Boundaries

> **Implementation status: Analysis.** Threat analysis; mitigations are claimed by the specs they reference, not here.


## Protected Assets

- Content and metadata of backup archives;
- Repository Master Key and key envelope wrappers (`KeyEnvelopeV1`);
- Sovereign recovery secrets and BIP-39 mnemonics;
- Endpoint device credentials and cloud storage access keys;
- Integrity of snapshots, retention policies, and cryptographic audit receipts;
- Operational availability of the autonomous disaster recovery pipeline (`Fortiq.Recover`).

---

## Threat Actors & Adversary Models

- **Local Malware / Ransomware**: Executing with standard user or local service permissions;
- **Compromised Endpoint Administrator**: Malicious or rogue administrator attempting to delete backup repositories;
- **Storage Credential Thief**: Adversary possessing read/write storage access keys to backup buckets;
- **Untrusted / Hostile Cloud Storage Provider**: Cloud provider attempting to inspect or alter stored ciphertext;
- **Network Adversary / Man-in-the-Middle (MitM)**: Attempting interception or tampering with repository data in transit;
- **Physical Device Thief**: Physical possession of a powered-off laptop or stolen paper recovery sheet;
- **Adversarial Content**: Injected prompts or corrupted documents designed to manipulate local AI copilots.

---

## Required Platform Behaviors

| Threat Scenario | Mandatory Platform Response |
| :--- | :--- |
| **Powered-off Laptop Stolen** | Stored repository ciphertext and reusable encryption keys remain unexposed. |
| **Endpoint Ransomware Infection** | Immutable snapshots in Object Lock compliance mode remain uncorrupted and indestructible. |
| **TPM-Bound Device Destroyed** | Full repository access is restored via independent sovereign recovery mnemonic (`Bip39RecoveryEnvelopeV1`). |
| **Cloud KMS Outage** | System enforces strict, deterministic fail-closed policy rather than falling back to unencrypted operation. |
| **Local Catalog Database Lost** | Entire repository can be inspected, unlocked, and restored offline via `Fortiq.Recover`. |
| **Storage Infrastructure Compromised** | Unauthorized modifications or blob truncations are detected immediately by cryptographic verification. |
| **Mnemonic Sheet Compromised** | Optional passphrase / BIP-39 passphrase salt prevents unauthorized unwrap of the master key. |
| **Prompt Injection in Backed-up Files** | Local AI components possess zero execution privileges and operate strictly read-only on sanitized metadata. |
| **Vendor Dissolution / Fortiq Shutdown** | Open-source, zero-dependency `Fortiq.Recover` CLI continues restoring datasets indefinitely. |

---

## Explicit Non-Guarantees (Out of Scope Without Additional Controls)

- Protection of unencrypted in-memory data on a fully compromised, live-running endpoint with kernel rootkits;
- Complete masking of low-level block size metadata (deduplicated pack file sizes are visible to storage operators);
- Automated legal compliance certification solely through software installation;
- Recovery following the catastrophic simultaneous loss of all recovery mnemonics, hardware keys, and passphrases;
- Application-consistent state for complex databases if the corresponding VSS writer is absent or disabled.

---

## Pre-Production Security Gates

Every gate below is executable from this repository. Gates that named an outside firm — penetration
testing, independent third-party audit — were removed in favour of the adversarial agent audit defined
in [ADR-013 Revision 1](adr/ADR-013-argon2-dependency-policy.md), which the project can actually run,
repeat and commit evidence for. A gate nobody here can execute does not raise the bar; it stalls the
work it guards while being quoted as the reason the work has not happened.

- Documented cryptographic design review (ADR-002, ADR-013);
- Adversarial agent audit of `Fortiq.Recover` and the IPC brokers, under the independence, grounding,
  verification and committed-evidence rules of ADR-013 Revision 1;
- Fuzz testing of repository metadata JSONL parsers and CBOR decoders;
- Regular simulated disaster drills (total catalog and control-plane loss);
- Ransomware resilience simulation with active delete-marker injection, run against the local MinIO
  cluster from `scripts/Get-TestStorage.ps1`;
- Cryptographic supply-chain attestation: CycloneDX SBOM, signed commits, and build provenance
  attestation. Authenticode signing of Fortiq binaries requires a code-signing certificate this project
  does not hold; releases are published unsigned and say so, rather than published as though signed.

---

## Object Storage Credentials

The identity Fortiq uses to reach a bucket is not the repository's encryption secret. The repository
is encrypted before anything is uploaded, so an attacker holding these keys can see how much data
there is and, unless the bucket forbids it, delete it — but cannot read it. The two are meant to be
held differently, and until recently they were not: storage keys came from `AWS_ACCESS_KEY_ID` and
`AWS_SECRET_ACCESS_KEY` in the process environment.

That had four problems, and only the first is obvious:

1. A Windows service does not inherit the environment somebody typed the keys into.
2. One set of keys served every repository, so an identity issued to write to one bucket reached
   all of them.
3. Rotation meant editing machine environment variables and restarting.
4. The secret sat where anything able to enumerate a process environment could read it.

`StoredObjectStorageCredentials` holds them per repository, keyed on the normalised repository
location and encrypted with DPAPI at machine scope. `Fortiq.Service credentials set` writes one,
reading the secret from standard input — a command line is visible in the process list and survives
in shell history.

### What this does and does not protect against

| Threat | Covered |
| :--- | :--- |
| Disk removed from the machine | Yes — DPAPI machine scope binds the ciphertext to this Windows installation |
| File copied to another machine | Yes |
| Credential file swapped for another repository's | Yes — the subject is bound into the ciphertext and checked on read |
| Secret visible in the process list or shell history | Yes — read from standard input |
| Unrelated standard account on the same machine reading a newly written credential | Restricted by the owner/SYSTEM/Administrators ACL |
| Directory owner, SYSTEM or administrator reading the file | **No isolation** — these identities are trusted |

DPAPI at machine scope means any process that can open the file can also unprotect it. Writes now
create a protected ACL for the directory owner, SYSTEM and Administrators. A new directory retains
the creating operator as owner so a standard operator can read what they wrote. Reads reject broad
file or directory grants; legacy credentials with explicit broad grants must be replaced using
`credentials set`. Existing directory ownership is preserved. Credentials created under a different
owner may require that owner or an administrator to migrate them. Arbitrary service identities and
installer-wide state permissions remain unimplemented; do not infer service installation readiness
from this credential-store policy.
