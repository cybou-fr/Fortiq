# ADR-013: Argon2id Dependency Selection & Cryptographic Supply-Chain Policy

- Status: **Active Review Gate** (Implementation of production `PasswordEnvelopeV1` blocked until formal vetting passes)
- Date: **September 3, 2026**
- Revision 1: **September 5, 2026** — the review gate is discharged by a reproducible adversarial agent audit run from this repository, not by commissioning an outside firm.
- Scope: `PasswordEnvelopeV1`, cryptographic dependencies, and supply-chain governance

---

## Context

ADR-002 mandates the use of Argon2id v1.3 (RFC 9106) for password-derived key envelopes. However, selecting a cryptographic implementation cannot be guided merely by package download counts on NuGet. Fortiq must verify:
- Constant-time properties and resistance to side-channel analysis;
- Correctness against official RFC 9106 test vectors;
- Origin and integrity of underlying native binaries;
- Memory zeroization capabilities across managed and native boundaries;
- Long-term maintenance vitality.

---

## Candidate Evaluations

### 1. `Sodium.Core` / libsodium
- **Advantages**: Implemented by widely-audited native cryptographic runtime (libsodium); robust cross-platform packaging.
- **Constraints**: High-level `crypto_pwhash` abstracts away explicit parameterization (such as specific degree of parallelism `p`), which may conflict with strict envelope parameter serialization.

### 2. `Konscious.Security.Cryptography.Argon2`
- **Advantages**: Pure managed .NET implementation offering direct parameter control (`m`, `t`, `p`); simple self-contained bundling.
- **Constraints**: Managed garbage-collected runtimes do not provide deterministic guarantees against intermediate memory copying before zeroization; requires rigorous independent security audit.

### 3. In-House Implementation
- **Rejected**: Fortiq strictly prohibits writing custom cryptographic implementations from scratch.

---

## Decision & Release Gates

1. **Simulated Envelope for Test Seams**: Active development utilizes test-only envelope providers; production builds gate password envelope activation on formal dependency approval.
2. **Mandatory Vetting Gates**:
   - Verification against official RFC 9106 vectors;
   - Negative boundary testing for salt length, memory allocation limits, and parallel lanes;
   - Cross-architecture packaging validation on Windows x64 and ARM64;
   - Complete SBOM generation and package hash locking via `Directory.Packages.props`;
   - **Adversarial agent audit** (Revision 1).

---

## Revision 1 — Discharging the Review Gate

The original fifth gate read *independent external cryptographic review*. It was unreachable: it names
a purchase, not a piece of work, and a gate nobody in the project can execute does not block a
dependency — it blocks the ADR, and the envelope stays unbuilt indefinitely while the gate is quoted as
the reason. `PasswordEnvelopeV1` has been the only unbuilt envelope for that whole time.

The gate is replaced by an **adversarial agent audit**: a reproducible, version-controlled review of the
candidate dependency carried out by AI agents against a fixed brief, with the findings committed as
evidence. It is discharged in the repository, by the project, on demand.

### What the audit must produce

1. **Independence by construction**: at least three agents review the same candidate from separate
   briefs — implementation correctness, memory and side-channel behaviour, and supply-chain provenance —
   without sight of each other's findings. Agreement across independent briefs is the signal; a single
   agent's opinion is not.
2. **Grounded findings only**: every finding cites the specific source file, line and version of the
   dependency it concerns. A finding that cannot name where it lives is discarded before review.
3. **Verification pass**: each surviving finding is re-checked adversarially against the source, and
   recorded as confirmed or withdrawn. Unverified findings never reach the decision.
4. **Committed evidence**: the brief, the model and version used, the dependency version audited and
   the confirmed findings are written to `docs/audits/` and referenced from this ADR. An audit whose
   inputs are not recorded cannot be repeated, and an audit that cannot be repeated is an anecdote.
5. **Re-run on change**: the audit is repeated whenever the dependency version, its native binaries or
   the envelope parameters change. The recorded dependency version is what makes that check mechanical.

### What this audit is not

It is not a human cryptographer's review, and this ADR does not claim it is. An agent audit reads code
and reasons about it; it does not run differential power analysis, it does not time constant-time
claims on real silicon, and it will not find a flaw that requires understanding a lattice.

What it does cover is the class of defect that actually reaches a .NET application through a NuGet
package: wrong parameter serialization, a managed buffer that is never cleared, a native binary of
unstated origin, a version that silently diverges from the pinned hash. Those are the failure modes
gates 1–4 already probe mechanically, and the audit is the reasoning layer over the same ground.

Treating this as sufficient is a deliberate risk acceptance, recorded here so it is visible rather than
implied. If Fortiq later handles third-party data under a contract that requires a named human
reviewer, this gate is reopened — the audit evidence in `docs/audits/` is the starting material for it,
not a substitute the contract will accept.
