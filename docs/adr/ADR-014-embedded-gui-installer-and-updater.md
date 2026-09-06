# ADR-014: Embedded GUI Installer, System Discovery & Autonomous Component Updater

- Status: **Accepted Architecture**
- Date: **September 4, 2026**
- Scope: Application packaging, system installation discovery, prerequisite verification, and in-app component update lifecycle

---

## Context

Enterprise backup solutions conventionally rely on third-party Windows installer engines (such as Windows Installer / MSI or InnoSetup). While suitable for initial deployments, external installers introduce significant operational friction:
1. **Opaque Installation State**: An installed application cannot easily introspect or repair individual sub-components (such as a corrupted third-party storage engine binary, broken service SID permissions, or outdated password helpers) without executing an external setup program.
2. **Disconnected Component Lifecycle**: Fortiq depends on external components (specifically the pinned `restic` engine, Microsoft Platform Crypto Provider for TPM 2.0, Volume Shadow Copy Service, and isolated Named Pipe helpers). Traditional installers bundle or drop these components statically, offering no continuous runtime mechanism to verify their integrity or update them independently.
3. **Double-Click Friction**: Users downloading a backup application expect an immediate, clean experience: either run as an ad-hoc portable recovery tool, or install as a permanent system service with one click from the interface itself.
4. **Update Vulnerabilities & Service Disruption**: Updating background services while keeping immutable repositories safe requires transactional orchestration: stopping the service, swapping pinned binaries, updating cryptographic manifests, re-verifying file locks, and verifying health before finalizing the update.

---

## Decision

Adopt an **Embedded GUI Installer & Component Lifecycle Architecture** directly integrated into `Fortiq.Desktop`:

1. **Dual-Mode Executable Architecture**:
   - `Fortiq.Desktop.exe` is a single cohesive binary capable of operating in **Portable Mode**, **Setup Mode**, or **Installed Mode**.
   - Upon launch, the application queries an installation inspector (`IInstallationInspector`) to discover whether Fortiq is installed in `%ProgramFiles%\Fortiq`, whether the Windows Service `Fortiq` is registered, and whether `%ProgramData%\Fortiq` is provisioned.
   - If uninstalled, the application launches an interactive **Installation & Prerequisite Wizard** rather than exiting or failing silently.

2. **Strongly-Typed System Inspection**:
   - The platform evaluates a comprehensive status model (`SystemInstallationStatus`):
     - Installation path and executable identity;
     - Windows Service status (`Installed`, `Running`, `Stopped`, `ServiceAccountSid`);
     - Pinned restic engine status (Version, SHA-256 binary validation against `manifest.json`);
     - Isolated password helper binary integrity and handle pinning (`Fortiq.PasswordHelper.exe`);
     - Operating system capabilities: TPM 2.0 readiness, VSS writer permissions (`SeBackupPrivilege`), .NET runtime compatibility.

3. **UAC Elevation Boundary & Least-Privilege Service Setup**:
   - Installation and system-level updates request elevation via standard Windows UAC (`runas` verb on an internal worker command).
   - The installer creates the Windows Service under `LocalSystem` and adds an unrestricted **Service
     SID** (`NT SERVICE\Fortiq`) to its token. The SID is an identity for ACLs, not a reduction in
     privilege: the service holds what `LocalSystem` holds. This decision recorded the opposite until
     it was checked against a running installation.
   - Restrictive NTFS DACLs are configured:
     - Program files (`%ProgramFiles%\Fortiq`): Read/Execute for Users, Write for Administrators/SYSTEM only.
     - State root (`%ProgramData%\Fortiq`): Restricted strictly to `SYSTEM` and `NT SERVICE\Fortiq`.

4. **TUF-Aligned In-App Component Updates**:
   - Updates are evaluated against signed TUF release metadata (`release-manifest.json`) carrying monotonically increasing `releaseSequence` numbers to defeat rollback attacks (ADR-008).
   - **Not to be confused with `engines/manifest.json`**, which pins the third-party storage engine. That file is unsigned, ships in the repository, and carries schema `fortiq.engine-manifest` with no `releaseSequence`. The two have different contents and, more importantly, different trust properties: one is verified by signature, the other by a hash the repository itself asserts. Giving them the same filename, as an earlier draft did, is an invitation to verify one and trust the other.
   - Component updates are atomic:
     1. Stage new binaries in `%ProgramData%\Fortiq\updates\staging\<version>\`;
     2. Verify SHA-256 hashes and Authenticode digital signatures;
     3. Gracefully stop `Fortiq.Service`;
     4. Atomically swap binaries in `%ProgramFiles%\Fortiq\`;
     5. Restart `Fortiq.Service`;
     6. Execute an immediate health verification: wait for the restarted service to publish a **fresh** `health.json` — one whose `producedAt` is later than the restart — and read it. `HealthAssessor.Assess` is a pure function over facts somebody else gathered; it cannot tell you whether a service came back, and naming it here would send an implementer to the wrong seam.
     7. **Automatic Rollback**: If the service fails to start or health evaluation reports critical errors within 60 seconds, the previous binaries are automatically restored.

5. **Headless / Unattended Administration**:
   - The same executable provides command-line flags for enterprise orchestration (Intune, SCCM, Ansible):
     - `Fortiq.Desktop.exe --status [--json]`
     - `Fortiq.Desktop.exe --install [--silent] [--dir <path>]`
     - `Fortiq.Desktop.exe --update [--silent]`
     - `Fortiq.Desktop.exe --uninstall [--purge-data]` — local state is **kept** unless purging is asked for. An earlier draft of this ADR spelled it `--keep-data`, which made deletion the default for a flag that destroys the audit trail; see [Spec 21](../21-embedded-installer-and-updater.md) for what purging removes.

---

## Consequences & Guarantees

- **Zero Secret Exposure**: The installer and updater never request, touch, or hold backup master keys, BIP-39 mnemonics, or S3 cloud credentials.
- **Repository Invariance**: Installing, updating, or uninstalling Fortiq components never mutates or deletes customer backup repositories.
- **Sovereign Portability**: The emergency recovery CLI (`Fortiq.Recover`) remains completely decoupled and functional without installing the desktop suite or Windows service.
