# Supply-Chain Security & Update Trust

> **Implementation status: Partially implemented.** Pinned engine manifest, TOCTOU handle locking, CycloneDX SBOM and provenance attestation exist. Authenticode signing of Fortiq binaries and the update channel do not.


## Objective & Security Mandate

The update and release verification subsystem guarantees that:
1. No arbitrary, unverified, or tampered binaries can be installed or executed;
2. Component mix-and-match across mismatched release packages is strictly blocked;
3. Rollbacks to known vulnerable versions are mathematically prevented via monotonically increasing release sequences;
4. Storage engines (`restic`), emergency recovery tools (`Fortiq.Recover`), and password brokers cannot be replaced by ambient binaries;
5. The updater process never possesses backup encryption keys or cloud storage credentials.

---

## Verified Engine Lifecycle & TOCTOU Elimination

Implemented via `EngineManifest.cs` and `EngineBinaryVerifier.cs` (`Fortiq.Infrastructure.Restic`):

1. **Manifest Pinning (`engines/manifest.json`)**:
   - Explicitly records restic version (`0.19.1`), RID (`win-x64`), expected binary byte length, SHA-256 archive hash, and SHA-256 binary hash.
2. **Download & Archive Validation (`scripts/Get-Engine.ps1`)**:
   - Verifies the archive SHA-256 before extraction;
   - Verifies binary byte length and SHA-256 immediately after extraction. Any mismatch purges all files from disk and aborts.
3. **Handle-Locked Pre-Execution Verification**:
   - `EngineBinaryVerifier` opens a `FileStream` with `FileShare.Read` and computes the cryptographic SHA-256 hash through that held handle.
   - The open handle prevents any concurrent process from modifying, deleting, or renaming the executable.
4. **Volume Serial & File Index Matching**:
   - Immediately prior to `Process.Start`, Fortiq re-opens the target path and queries operating system file identity (`VolumeSerialNumber` and `FileIndex`).
   - Comparing this identity against the verified open handle defeats directory-junction swapping and filesystem namespace race conditions (TOCTOU).

---

## Release Unit Composition

A Fortiq production release is signed and distributed as an atomic, cohesive unit:

```text
Fortiq Release Package
├── Fortiq.Desktop (Avalonia UI)
├── Fortiq.Service (Windows Service Worker)
├── Fortiq.Platform.Windows (Privileged Broker)
├── Fortiq.PasswordHelper (Isolated Named Pipe Broker)
├── Fortiq.Recover (Zero-Dependency Emergency CLI)
├── Pinned restic engine binaries (verified against manifest.json)
├── CycloneDX Software Bill of Materials (SBOM)
└── Cryptographic release signatures and provenance attestations
```

---

## The TUF-Aligned Update Trust Model (The Update Framework)

The update verification engine implements TUF security roles:
- **`root`**: Offline root trust keys establishing signature thresholds;
- **`targets`**: Cryptographic hashes, exact byte lengths, and platform compatibility matrices;
- **`snapshot`**: Version consistency matrix preventing mix-and-match component attacks;
- **`timestamp`**: Monotonic freshness counter preventing freeze/replay attacks.

Monotonically increasing `releaseSequence` numbers prevent downgrade attacks. Rollbacks are managed by issuing a **new forward release** with an incremented sequence number carrying previous stable binaries and documented migration steps.

---

## Release Evidence & Authenticode Signatures

- Release artifacts are packaged via `scripts/New-ReleaseArtifacts.ps1`, generating CycloneDX SBOMs and SHA-256 manifests.
- Windows PE binaries undergo Authenticode digital signature validation via `AuthenticodeSignature.cs`.
- The password helper broker enforces `RequireSignedHelper` mode in production environments to reject unsigned or untrusted helper binaries.
- The runtime installation and in-app component update workflow is governed by [Spec 21: Embedded GUI Installer & Component Lifecycle](21-embedded-installer-and-updater.md) and [ADR-014](adr/ADR-014-embedded-gui-installer-and-updater.md).
