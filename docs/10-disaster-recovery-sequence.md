# Autonomous Disaster Recovery Runbook (DR-001)

> **Implementation status: Partially verified.** The Windows x64 CLI flow is covered by separate-process
> recovery tests with cleared local working state. This is not evidence of a drill on a second pristine
> machine; that acceptance check remains required, and is discharged by the CI job described below
> rather than by a manual drill nobody schedules.


## Canonical Scenario: Total Endpoint & Control-Plane Loss

DR-001 represents the defining acceptance vector of Fortiq: **complete restoration on a pristine machine without Fortiq cloud services, active directory, or vendor involvement.**

### Scenario Preconditions
- The primary computer is physically destroyed, encrypted by malware, or permanently inaccessible.
- Local hardware TPM and all local state storage and catalogs are lost.
- Fortiq Windows Service, desktop UI, and corporate accounts are unavailable.
- The encrypted backup repository survives in object storage or secondary media.
- The operator possesses the **Recovery Kit** (`kit.json` + envelope files) and the secret BIP-39 mnemonic phrase.
- A Windows x64 computer with access to the repository storage is available. The current executor selects the pinned `win-x64` engine; Linux/macOS are not supported recovery targets by this implementation.
- The self-contained recovery distribution, password helper and matching pinned engine are available on separate media. A kit does not contain these executables.
- For S3, independent storage access credentials are available through `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and, if needed, `AWS_DEFAULT_REGION`. The recovery mnemonic unlocks encryption, not the storage account. The CLI does not read the original machine's DPAPI store.

---

## Autonomous Recovery Execution Sequence

```text
Operator          Fortiq.Recover CLI         Storage Backend        Restic Engine
   │                       │                        │                     │
   │ 1. inspect kit        │                        │                     │
   ├──────────────────────►│ 2. validate manifest   │                     │
   │                       ├───────────────────────►│ read config         │
   │                       │◄───────────────────────┤                     │
   │ 3. review identity    │                        │                     │
   │◄──────────────────────┤                        │                     │
   │ 4. input mnemonic     │                        │                     │
   │    (via stdin only)   │                        │                     │
   ├──────────────────────►│ 5. unwrap EUS via KDF  │                     │
   │                       │    (BufferKeyLease)    │                     │
   │                       │ 6. start restic with   │                     │
   │                       │    --password-command  │                     │
   │                       ├─────────────────────────────────────────────►│
   │                       │                        │◄── authenticated ───┤
   │ 7. select snapshot    │◄─────────────────────────────────────────────┤
   │    & target directory │                        │                     │
   ├──────────────────────►│ 8. restore into isolated staging            │
   │                       ├─────────────────────────────────────────────►│
   │                       │◄── verify reparse points / path bounds ──────┤
   │                       │ 9. atomic rename to target                   │
   │ 10. verify report     │ 11. zeroize secret buffers                   │
   │◄──────────────────────┤                                              │
```

---

## Recovery Kit Structure (`kit.json`)

The recovery kit directory contains open metadata (`kit.json`) alongside deterministic CBOR key envelope files:

```json
{
  "schema": "fortiq.recovery-kit",
  "version": 1,
  "repositoryId": "7c8e2a1b4d9f41f89a3b6e2c1d0e8f4a7c8e2a1b4d9f41f89a3b6e2c1d0e8f4a",
  "repositoryLocator": "s3:https://s3.eu-central-1.amazonaws.com/company-sovereign-backup",
  "engine": {
    "name": "restic",
    "version": "0.19.1",
    "sha256": "<64-character SHA-256 of the pinned engine>"
  },
  "storageProtection": {
    "immutable": true,
    "mode": "Compliance",
    "retentionDays": 365
  },
  "unlockMethods": [
    {
      "file": "bip39-<envelopeId>.cbor",
      "providerType": "bip39",
      "suite": "bip39-pbkdf2-hmac-sha512-hkdf-sha256-aes256gcm-v1",
      "envelopeId": "<hexadecimal envelope identifier>",
      "sha256": "<64-character SHA-256 of the envelope file>"
    }
  ],
  "createdAt": "2026-09-03T12:00:00Z",
  "instructions": "Use Fortiq.Recover with this kit and enter the mnemonic on standard input."
}
```

---

## Recovery CLI Command Reference (`Fortiq.Recover`)

The JSON above illustrates the current field names; placeholders are not a usable kit. Preserve the
actual generated `kit.json` and every referenced CBOR file. Do not reconstruct a kit by copying this example.

The autonomous recovery executable (`Fortiq.Recover.exe`) supports four core operations:

### 1. Inspect Repository & Kit
Parses open envelope metadata, repository format, and storage protection guarantees without requiring key material:
```powershell
Fortiq.Recover inspect --repository <repo-path-or-s3-url> --engine-root <engines-dir> [--kit <kit-dir>]
```

### 2. Query Snapshots
Lists available backup snapshots, timestamps, source paths, and point-in-time consistency flags:
```powershell
Fortiq.Recover snapshots --repository <repo> --engine-root <engines> --kit <kit-dir>
```

### 3. Verify Repository Integrity
Performs structural index checks and cryptographic blob verification:
```powershell
Fortiq.Recover check --repository <repo> --engine-root <engines> --kit <kit-dir>
```

### 4. Restore Dataset
Restores the chosen snapshot into an empty destination directory:
```powershell
Fortiq.Recover restore --repository <repo> --engine-root <engines> --kit <kit-dir> `
                       --snapshot <id> --target <empty-dir> [--source <original-path>]
```

> [!IMPORTANT]
> **Zero Credential Exposure**:
> `Fortiq.Recover` reads the mnemonic phrase strictly from standard input (`Console.In`). Command-line flags such as `--password`, `--secret`, or `--mnemonic` are rejected by the parser to prevent leakage in process argument tables or terminal history.

---

## Deterministic Exit Codes

### Deployment acceptance on a second Windows machine

This is a separate acceptance gate from the automated same-host tests, and it runs as a scheduled CI
job on a fresh `windows-latest` runner. That runner is the pristine machine the gate asks for: it has
never had Fortiq Service or Desktop installed, holds none of the development host's state, and is
discarded afterwards. Writing the gate as a manual instruction was the reason it had never been
executed — a drill that depends on somebody finding a spare machine is a drill that does not happen.

The job consumes only the published release artifacts and a kit generated in an earlier step, exactly
as an operator would:

1. Copy the self-contained recovery distribution, helper, pinned engine directory and generated kit
   to a fresh Windows x64 machine without Fortiq Service or Desktop.
2. Make the repository reachable; for S3 obtain storage credentials independently of the lost machine.
3. Run `inspect`, then `snapshots` and `check`, entering the mnemonic only on standard input.
4. Restore a selected snapshot into an empty disposable destination with `restore`.
5. Compare restored relative paths, sizes and SHA-256 hashes with an independently retained dataset
   manifest. Retain exit codes and reports without recording the mnemonic or storage secrets.
6. Record OS version, tool and engine hashes, elapsed restore time, dataset size and pass/fail result.

The separate-process tests prove this workflow on the development host; they do not substitute for
executing these steps on a machine that has never held Fortiq state. The CI job is what closes that
gap, and its recorded result in step 6 is the evidence — not this document's description of it.

### Two lanes, and what each one is evidence of

`PilotCoreWorkflowTests` runs on a hosted runner and covers the workflow: bundle integrity,
provisioning, hash-chained receipts, the restore drill, autonomous `Fortiq.Recover`, and tamper
detection. It installs with no service and no ACLs and uses a user-scoped key, so it is evidence about
the workflow and not about the deployment.

`InstalledWindowsPilotTests` is the second lane, and covers what needs an elevated session: an
installation that deploys the bundle it verified, state-directory ACLs that close `schedules`,
`credentials`, `audit` and `receipts` to ordinary users, and service registration through the SCM with
a computable service SID. It is run by `scripts/Test-InstalledPilot.ps1`, which **fails when the lane
skips**. That matters more than it sounds: these tests skip without elevation, a skipped test exits
zero, and a lane that quietly does nothing reports the same green as one that passed.

Three things remain unverified by any lane, and belong on the pilot's own acceptance checklist:

1. **A reboot**, and the service returning by itself afterwards.
2. **IPC refused across a pipe to a genuinely unelevated caller.** The runner is elevated, so the
   refusal is covered against the policy (`ServiceIpcAuthorizationTests`) rather than through a pipe.
3. **A machine that has never held Fortiq state.** A fresh runner is close, but the lanes run beside
   everything else on it.

`scripts/Test-PilotWorkflow.ps1` prints both lists rather than a single verdict, and fails when the
pilot test skipped rather than reporting a pass having run nothing.

### Exit code reference

`Fortiq.Recover` returns standard deterministic exit codes (`RecoveryCli.cs`):

| Code | Constant | Meaning |
| :---: | :--- | :--- |
| **0** | `ExitSuccess` | Operation completed successfully. |
| **64** | `ExitUsage` | Command-line usage error (unknown command, missing required option, or syntax error). |
| **69** | `ExitDataError` | Data error or rejected restore (corrupted repository, invalid payload, path traversal violation, or non-empty target). |
| **75** | `ExitRepositoryBusy` | Repository is actively locked or leased by another concurrent Fortiq run. |
| **77** | `ExitUnlockFailed` | Uniform authentication failure (invalid mnemonic passphrase, corrupted envelope, or unreadable key). No details leak. |
| **78** | `ExitKitMismatch` | Recovery kit does not match the target repository identifier or structure. |

