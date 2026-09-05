# ADR-008: TUF-Aligned Trust Model for Releases & Updates

- Status: **Accepted Architecture**
- Date: **September 3, 2026**
- Scope: Online/offline updates, binary release provenance, and Authenticode integration
- Revision 1: **September 5, 2026** — the root role holds software keys generated and kept offline by the release holder, and Authenticode is a verified-when-present signal rather than a mandatory gate. Both changes remove a purchase this project cannot make from the path to a working updater.

---

## Context

TLS transport security and code signing (Authenticode) alone fail to counter the complete software supply-chain threat model. An attacker compromising a download server can serve outdated, vulnerable binaries that still carry valid digital signatures, or selectively freeze updates to prevent security patches.

---

## Decision

Adopt **The Update Framework (TUF)** security architecture, with Microsoft Authenticode validation applied to Windows binaries that carry a signature.

1. **Four-Role TUF Architecture**:
   - `root`: Root of trust held as software keys, generated on an air-gapped machine and stored offline, under multi-signature threshold rules. Hardware key custody is an upgrade the threshold scheme already accommodates, not a precondition for shipping the updater;
   - `targets`: Explicit inventory of authorized artifacts, cryptographic hashes, and byte lengths;
   - `snapshot`: Release consistency matrix preventing mix-and-match component vulnerabilities;
   - `timestamp`: Monotonic freshness counter preventing replay and freeze attacks.
2. **Verification Gates**:
   - Every executable must match its TUF `targets` entry by hash and byte length. This gate is mandatory and is the one Fortiq controls end to end.
   - Authenticode signatures are verified when present (`AuthenticodeSignature.cs`), and a binary carrying an *invalid* signature is rejected outright. An *absent* signature is recorded in the update receipt, not treated as a failure: Fortiq holds no code-signing certificate, and a gate that rejects every build this project produces would only be satisfied by disabling it. Requiring a valid signature becomes correct the day a certificate exists, and is a one-line change to this policy — not a redesign.
3. **Monotonic Sequences & Rollbacks**:
   - Downgrade attacks are blocked by monotonically increasing release sequences.
   - Emergency rollbacks are packaged as **new forward releases** carrying older stable binaries with an incremented sequence number.
4. **Updater Isolation**:
   - The updater agent possesses zero access to backup encryption keys, recovery mnemonics, or storage access credentials.
