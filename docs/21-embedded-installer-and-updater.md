# Embedded GUI Installer, System Discovery & Component Lifecycle

> **Implementation status: Partially implemented.** The setup wizard (`InstallWindow`, `InstallViewModel`),
> system discovery (`InstallationInspector`), service registration (`WindowsServiceController`), bundle
> validation and the headless CLI (`InstallationCli`) exist.
>
> The updater's engine is built. `TufTrustedMetadata` decides whether a binary is authorised — refusing
> rollback, freeze and mix-and-match under [ADR-008 Revision 1](adr/ADR-008-update-trust.md);
> `UpdateTransaction` stages, swaps and rolls back crash-safely; `ComponentUpdater` joins the two, and
> `Fortiq.Service` resolves an interrupted update before it runs any scheduled work.
>
> What is missing is the **delivery and the surface**: no implementation of `IUpdateContentSource`
> fetches from anywhere, no release publishes TUF metadata, and there is no updates manager in the
> desktop. The section *In-App Component & Updates Manager* remains design intent.
>
> Read the *State Directory Layout & Access* and *Device Key Scope* sections before writing any of
> it. Both record a way this specification could be implemented exactly and still produce a machine
> that does not work: an ACL that locks the desktop out of its own state, and a readiness screen
> that reports green while the service is unable to open any repository.


## Objective & Principles

Fortiq eliminates the dichotomy between "portable tools" and "installed enterprise services". A single desktop application (`Fortiq.Desktop`) manages the entire application lifecycle:
1. **Self-Introspection**: Detects whether Fortiq is installed on the host system, inspects current version numbers, and evaluates the operational status of all platform services and third-party dependencies.
2. **Embedded Installation**: Automatically launches an interactive setup wizard when run on an unconfigured machine, installing binaries, registering the Windows Service with least-privilege service SIDs, and setting secure directory ACLs.
3. **Continuous Component Lifecycle & Self-Update**: Inspects, validates, and updates individual sub-components (core binaries, Windows background service, and third-party storage engines like restic) in an atomic, rollback-safe transaction under The Update Framework (TUF).
4. **Sovereign Portability Invariant**: Even when installed as a system-wide background service, the core emergency recovery CLI (`Fortiq.Recover`) remains 100% self-sufficient and portable.

---

## Operating Modes

`Fortiq.Desktop.exe` detects its operating environment upon launch and dynamically enters one of four operational modes:

```text
Fortiq.Desktop.exe Launched
 │
 ├── [CLI Flag: --status | --install | --update | --uninstall] ──► Headless Automation Mode
 │
 └── [Interactive GUI Launch]
      │
      ├── Target: Not Installed (running from Temp/Downloads/USB) ─► Embedded Setup Wizard Mode
      │
      ├── Target: Installed in %ProgramFiles%\Fortiq
      │    │
      │    ├── Outdated Components / Update Available ────────────► Component Update Notification
      │    │
      │    └── Everything Up-to-Date & Healthy ──────────────────► Main Recovery-First UI
      │
      └── [User explicitly selects "Portable Mode"] ──────────────► Ad-Hoc Recovery / Inspect Mode
```

### 1. Embedded Setup Wizard Mode
Entered when `Fortiq.Desktop.exe` is executed outside `%ProgramFiles%\Fortiq` and no local service registration is detected. Offers a clean, modern guided experience to install the platform, provision background services, and verify hardware prerequisites.

### 2. Installed Mode
The standard operating mode when executed from the official installation path (`%ProgramFiles%\Fortiq\`). Loads the primary dashboard, displays repository health (`Recoverable`, `Unproven`, `AtRisk`), and provides an embedded **"Components & Updates"** management pane.

### 3. Portable Mode
Permits running Fortiq directly from external media (USB drive, network share) without writing registry keys or registering Windows services. Designed for disaster recovery personnel performing bare-metal recovery drills or ad-hoc repository inspections.

### 4. Headless Automation Mode
Triggered via command-line arguments (`--status`, `--install`, `--update`, `--uninstall`). Used by enterprise system administrators for silent orchestration via Microsoft Intune, SCCM, or Ansible.

---

## System Discovery & Inspection Model

System inspection is coordinated via `IInstallationInspector`:

```csharp
public interface IInstallationInspector
{
    Task<SystemInstallationStatus> InspectAsync(CancellationToken cancellationToken);
}

public sealed record SystemInstallationStatus(
    bool IsInstalled,
    string? InstallationPath,
    string ExecutablePath,
    Version CurrentVersion,
    ServiceComponentStatus Service,
    EngineComponentStatus Engine,
    HelperComponentStatus PasswordHelper,
    PlatformPrerequisitesStatus Platform,
    IReadOnlyList<InstallationFinding> Findings);

public sealed record ServiceComponentStatus(
    bool Registered,
    bool Running,
    string? ServiceAccount,
    Version? Version,
    string? BinaryPath);

public sealed record EngineComponentStatus(
    string Name,
    string RequiredVersion,
    string? InstalledVersion,
    bool HashVerified,
    string BinaryPath);

public sealed record HelperComponentStatus(
    bool Exists,
    bool AuthenticodeVerified,
    string BinaryPath);

public sealed record PlatformPrerequisitesStatus(
    bool TpmAvailable,
    bool HasBackupPrivileges,
    bool DotNetRuntimeValid,
    string DotNetVersion);

public sealed record InstallationFinding(
    FindingSeverity Severity,
    string Component,
    string Message,
    string? RemediationAction);
```

### Prerequisite & Component Verification Matrix

| Component / Layer | Verification Mechanism | Success Criteria | Remediation / Fallback |
| :--- | :--- | :--- | :--- |
| **.NET Runtime** | Operating System Host API | .NET 10.0 LTS Desktop Runtime present (or self-contained bundle) | Prompts user or deploys self-contained runtime binaries |
| **Windows Service** | Windows Service Control Manager (`sc.exe` / Win32 API) | Service `Fortiq` registered with auto-start and Service SID | Self-installs via UAC elevation |
| **Service Account** | Windows LSA & Service SID | Service configured to run as `NT SERVICE\Fortiq` (least privilege) | Configures service SID during installation |
| **Storage Engine** | `manifest.json` & `EngineBinaryVerifier` | `restic.exe` present, length and SHA-256 match pinned manifest | Automatically runs verified acquisition pipeline (`Get-Engine.ps1`) |
| **Password Helper** | `PinnedFile` & `AuthenticodeSignature` | `Fortiq.PasswordHelper.exe` present with valid digital signature | Deploys pinned helper executable with restrictive DACL |
| **Hardware TPM** | Microsoft Platform Crypto Provider (`NCrypt`) | TPM 2.0 silicon present with non-exportable key storage support | Warns operator; falls back to BIP-39 mnemonic recovery only |
| **VSS Privileges** | Windows Security Token API | Process token or service SID possesses `SeBackupPrivilege` | Warns if running unprivileged; informs user live capture will be used |
| **State Storage** | NTFS Security Descriptor (DACL) | `%ProgramData%\Fortiq\` carries the per-directory ACLs in *State Directory Layout & Access* below | Corrects directory ACLs during elevated install |
| **Free Space** | Volume free space on the drill workspace | At least the drill floor (20 GB by default) free where restore drills stage | Warns that scheduled restore drills will not run until there is room |
| **Device Key Scope** | Microsoft Platform Crypto Provider (`NCrypt`) | The service account can open the repository's device key — see *Device Key Scope* below | Offers to re-provision the device key in the machine store, or states that unattended work is unavailable |

---

## State Directory Layout & Access

`%ProgramData%\Fortiq` is not one thing with one ACL. Three principals need it, and giving the whole
tree to two of them is what an earlier draft of this specification did:

- **`SYSTEM`** and the service account **`NT SERVICE\Fortiq`** — the unattended half. Backups,
  drills, retention, health publication.
- **Interactive operators** — the desktop runs as a signed-in person, not as the service. It reads
  the health report, writes a schedule file when somebody protects a folder, reads receipts when
  they press *Prove recovery*, and reads nothing else.

An ACL restricted strictly to `SYSTEM` and the service SID, as this document previously specified,
produces a green installation and a desktop that cannot function. The rights belong per directory:

| Directory | `SYSTEM` + `NT SERVICE\Fortiq` | Interactive operators |
| :--- | :--- | :--- |
| `schedules\` | Read | **Read/Write** — the protection wizard writes here |
| `state\` | Read/Write | Read |
| `work\` | Read/Write | — |
| `work
eceipts\` | Read/Write | Read — evidence is read by the desktop and by monitoring |
| `runs\` | Read/Write | **Read/Write** — a desktop-initiated drill registers a run like any other |
| `health\` | Read/Write | Read |
| `credentials\` | Read/Write | **None** |
| `updates\staging\` | Read/Write | — |

`credentials\` is the one directory operators are kept out of, and it is the one that most needs it:
storage secrets there are encrypted with DPAPI at machine scope, which means any account that can
*read* the file can decrypt it. The ACL is the access control; DPAPI is only the at-rest protection.
See [Spec 03](03-threat-model.md).

The paths themselves come from `FortiqStatePaths`, which every Fortiq process asks rather than
composing its own. An installer that invents its own layout will diverge from the running software.

---

## Device Key Scope

The prerequisite matrix checks that a TPM is present. That is not the question that decides whether
this installation works.

A repository's device key is created in a Windows key store, and the store decides who can open it.
A key created in the operator's profile cannot be opened by the service, whatever identity the
service runs as and however correct the recovery kit is. Since the service is the thing that runs
backups when nobody is signed in, a user-scoped device key means unattended work never happens —
and every check in the readiness screen still shows green.

The installer must therefore establish, and the wizard must state:

1. **Which scope the device key will be created in.** `DeviceKeyScope.Machine` is what a Windows
   service needs; creating one requires administrator rights, which the installer already has at
   that point in the flow and the desktop later does not.
2. **That the service account can open it.** Presence of a TPM is necessary and not sufficient. The
   check is an attempted open as the service identity, not a capability probe.
3. **What happens when it cannot.** Unattended backups are unavailable, and the repository is
   openable only with the recovery phrase. This is a legitimate configuration for a single-operator
   machine; it is not a legitimate thing to discover months later from a repository that has never
   been backed up.

`RepositoryProvisioner` takes the scope as a parameter and records it in the envelope, so an
existing kit can be asked which store its key is in rather than guessed at.

---

## Interactive GUI Workflows (`Fortiq.Desktop`)

### 1. First-Run Installation Wizard (`InstallWindow`)

The installation wizard follows a deterministic 4-step workflow:

```text
┌──────────────────────── Fortiq Installation Wizard ────────────────────────┐
│                                                                            │
│  Step 1: System Readiness Check                                           │
│  [✓] Operating System: Windows 11 Enterprise (Build 26100)                 │
│  [✓] .NET Runtime: 10.0 LTS Verified                                      │
│  [✓] Hardware TPM: TPM 2.0 Ready (Microsoft Platform Crypto Provider)     │
│  [✓] Storage Engine: restic 0.19.1 SHA-256 Validated                       │
│  [✓] VSS Snapshot Privileges: Available                                   │
│                                                                            │
│  Step 2: Installation Location & Options                                   │
│  Install directory: [ C:\Program Files\Fortiq                     ] [Browse]│
│  [✓] Install background service for automated scheduled protection        │
│  [✓] Add Fortiq command-line tools to system PATH                         │
│                                                                            │
│  [ Run as Portable ]                                  [ Install Fortiq -> ] │
└────────────────────────────────────────────────────────────────────────────┘
```

1. **Step 1 — System Readiness**: Automatically runs `IInstallationInspector`. Displays clear green checkmarks or remediation alerts (e.g. "TPM not found: software envelopes will be used").
2. **Step 2 — Location & Configuration**: Allows designating the target installation path (defaulting to `%ProgramFiles%\Fortiq`).
3. **Step 3 — Privilege Elevation**: Triggers a single Windows UAC prompt. The elevated worker:
   - Copies application binaries to `%ProgramFiles%\Fortiq\`;
   - Places the verified `restic.exe` binary in `%ProgramFiles%\Fortiq\engines\`;
   - Configures restrictive NTFS ACLs on `%ProgramFiles%\Fortiq` (Users: Read/Execute; Administrators: Full);
   - Creates `%ProgramData%\Fortiq\` and applies the per-directory ACLs from *State Directory Layout & Access* — not one ACL over the whole tree, which would lock the desktop out of its own state;
   - Registers and starts the Windows Service `Fortiq` under `NT SERVICE\Fortiq`;
   - Establishes that the service account can open a machine-scoped device key, so that unattended backups are possible before anybody is told the machine is ready;
   - Adds `%ProgramFiles%\Fortiq\` to system PATH.
4. **Step 4 — Onboarding Transition**: The wizard seamlessly closes and launches the standard `MainWindow` with the Protection Setup Wizard open.

---

### 2. In-App Component & Updates Manager

> *Design intent, not implemented.* No component or updates manager exists in `src/`; the desktop
> has no navigation entry for it.

Accessible via the application menu or top-level status bar:

```text
┌─────────────────────── Components & Update Status ────────────────────────┐
│                                                                            │
│  Installed Release: v1.0.4 (Current: Latest Stable)                        │
│                                                                            │
│  Component Status & Health:                                                │
│  ● Fortiq Desktop UI         v1.0.4     [ Up to date ]                     │
│  ● Fortiq Service            v1.0.4     [ Running (PID 1420) ]             │
│  ● Storage Engine (restic)   v0.19.1    [ Verified SHA-256 ]               │
│  ● Password Broker Helper    v1.0.4     [ Verified Authenticode ]          │
│  ● Hardware TPM Provider     TPM 2.0    [ Ready (1 Key Sealed) ]           │
│                                                                            │
│  [ Check for Updates ]                                   [ Repair System ] │
└────────────────────────────────────────────────────────────────────────────┘
```

If a new release or updated engine is discovered:
- The UI displays a notification: **"Update Available: v1.1.0"** along with release notes and component changes.
- A **"1-Click Update"** button executes the transactional atomic update sequence.

---

## Transactional Atomic Update Protocol & Rollback

> *Implemented.* `UpdateTransaction` performs this protocol through three directories on one volume —
> staging, backup, install — recording its position in `update-intent.json`, the same device
> `provisioning-intent.json` uses. Recovery does not need to know where a crash landed: every file in
> the backup directory is one whose original has not been put back, so restoring all of them is correct
> after one swap or all of them, and correct again when recovery is itself interrupted.

Updating a running disaster recovery service must never leave the machine in a broken or half-updated state. The update process executes as a transactional state machine:

```text
                    ┌─────────────────────────┐
                    │ 1. Download & Staging    │
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │ 2. Dual Verification    │
                    │ (SHA-256 + Authenticode)│
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │ 3. Service Quiescence   │
                    │ (Graceful stop, 30s)    │
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │ 4. Atomic Binary Swap   │
                    │ (Backup old to .prev)   │
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │ 5. Restart & Health Test│
                    │ (Await FRESH health.json│
                    │  newer than the restart)│
                    └────────────┬────────────┘
                                 │
                 ┌───────────────┴───────────────┐
                 │                               │
       [Health: OK (Recoverable/Unproven)]   [Health: FAILED / Crash]
                 │                               │
     ┌───────────▼───────────┐       ┌───────────▼───────────┐
     │ 6. Commit Update      │       │ 7. Automated Rollback │
     │ (Delete .prev backup) │       │ (Restore .prev & exit)│
     └───────────────────────┘       └───────────────────────┘
```

### Rollback Guarantees
- If the new service fails to start or crashes within 60 seconds of updating, the installer immediately halts, copies back the `.prev` binaries, and restarts the previous version.
- Active repositories and recovery kits (`kit.json`) are completely decoupled from application binaries; an update failure never corrupts or alters repository data blocks.

---

## Headless Automation & CLI Flags

For enterprise deployment, all installer and component management actions can be executed headlessly via `Fortiq.Desktop.exe`:

```powershell
# Query current installation and component status (returns JSON)
Fortiq.Desktop.exe --status --json

# Silent installation with default paths and service registration
Fortiq.Desktop.exe --install --silent

# Custom installation directory
Fortiq.Desktop.exe --install --dir "D:\Tools\Fortiq" --silent

# Check and apply available updates silently
Fortiq.Desktop.exe --update --silent

# Uninstall Fortiq service and program files. Local state is KEPT unless --purge-data is given.
Fortiq.Desktop.exe --uninstall --silent

# Also remove local state. Destructive: see the table below for what this deletes.
Fortiq.Desktop.exe --uninstall --purge-data --silent
```

### Open: Two Administrative Surfaces

This specification puts machine administration on `Fortiq.Desktop.exe` (`--status`, `--install`,
`--update`, `--uninstall`). Since it was written, storage credentials acquired their own commands on
a different binary: `Fortiq.Service credentials set|remove|list`, which lives there because that is
the process that reads them and it already resolves the same state directory.

Two admin surfaces on two executables is a decision, not an accident waiting to be tidied, and it
should be made before either grows further. Either the desktop becomes the single administrative
entry point and delegates to the service, or the service owns machine-level configuration and the
desktop owns installation. Whichever is chosen, one of the two documents currently describing them
is wrong.

---

### What `--purge-data` Removes

Keeping state is the default and the opt-out is explicit, because the flag deletes things that
cannot be recovered from the repository. ADR-014 previously described the inverse spelling
(`--keep-data`, implying purge by default); this is the reconciled form, and there is no
`--keep-data` flag.

| Removed by `--purge-data` | Consequence |
| :--- | :--- |
| `credentials\` | Storage keys must be supplied again. Correct to remove: they are secrets for this machine. |
| `schedules\`, `state\` | Configuration and run history. Backups stop; the repository is untouched. |
| `runs\`, `work\` | Scratch and lock state. No loss. |
| `health\` | Regenerated on the next pass. |
| **`work
eceipts\`** | **The audit trail.** The only record that backups, checks and restores happened, and what [Spec 15](15-audit-compliance.md) maps to compliance obligations. It exists nowhere else — not in the repository, not in the kit. |

Never removed by any uninstall flag:

- The repository itself, wherever the operator put it.
- The recovery kit (`kit.json`), which is the way back into the repository and by design is stored
  away from this machine.

Because receipts are evidence rather than state, `--purge-data` should offer to write them out
before deleting, and the uninstall summary must say plainly that the audit trail is being destroyed.
An operator removing an application does not expect to be removing their compliance record.

---

### Exit Codes for Automation
- `0`: Operation completed successfully.
- `64`: Command-line syntax error or invalid arguments.
- `65`: System prerequisites failed (e.g. incompatible OS or unsupported processor).
- `66`: UAC elevation was rejected by user or prohibited by policy.
- `70`: Component verification failure (SHA-256 mismatch or invalid digital signature).
- `75`: Service is currently busy executing an active backup or recovery run. *Determined by attempting an exclusive run in the run registry (`Fortiq.Infrastructure.Runs`), the same mechanism retention uses. There is no service-wide "busy" flag to query, and inventing one would give two answers that could disagree.*
- `80`: Update failed and automatic rollback was executed.

---

## Security & Least-Privilege Architecture

1. **UAC Boundary**: Elevation is requested exclusively when modifying `%ProgramFiles%`, registering Windows services, or configuring system ACLs. Daily usage and recovery drills run entirely under standard interactive user tokens.
2. **Service SID Isolation**: The Windows service runs under `NT SERVICE\Fortiq`. It does not require or receive broad `LocalSystem` administrative privileges.
3. **No Credential Custody During Updates**: The update subsystem has zero access to encryption keys, recovery secrets, or cloud bucket passwords.
4. **Authenticode Enforcement**: Only binaries signed with the trusted Fortiq code-signing certificate are accepted during self-update or component replacement.

   > **Blocking prerequisite, not a deployment mode.** No such certificate exists today, and release
   > signing is an open item on the roadmap. The whole self-update path rests on it: without a
   > signing key, a hash alone proves only that a binary is the one some manifest named. The updater
   > must not ship before the certificate does. An earlier draft hedged this as "in production
   > environments", which reads as a configuration choice rather than the gate it is.
