# ADR-003: Process Boundaries & Privilege Separation for V1

- Status: **Accepted & Implemented**
- Date: **September 3, 2026**
- Scope: Desktop UI, Windows Service, privileged platform operations, AI copilot, and CLI recovery

---

## Context

Running all orchestration, VSS coordination, network access, key management, and storage engine execution inside a monolithic Windows Service under `NT AUTHORITY\SYSTEM` introduces unacceptable blast radius: compromising a single parser or network socket would hand the adversary maximal local system privileges.

---

## Decision

Enforce strict process boundaries across five decoupled runtime components:

| Process | OS Privilege Context | Network Access | Key Access | Primary Responsibility |
| :--- | :--- | :---: | :---: | :--- |
| **`Fortiq.Desktop`** | Interactive user token | None (local IPC only) | None | Presentation, MVVM, user confirmation |
| **`Fortiq.Service`** | Dedicated service SID | Storage endpoints only | Ephemeral leases | Scheduling, orchestration, policy |
| **`Fortiq.Platform.Windows`** | Elevated platform token | None | None | VSS snapshot creation, USN journal hints |
| **AI Copilot (Phi Silica)** | Sandboxed user token | None | None | Read-only advisory explanations |
| **`Fortiq.Recover`** | Interactive operator | Storage endpoint only | Ephemeral lease | Autonomous offline disaster recovery |

External restic processes are spawned as sandboxed child processes using stripped environment blocks and ephemeral named pipe credentials (`--password-command`).

---

## Architectural Invariants

- `Fortiq.Desktop` cannot connect directly to privileged Windows platform brokers.
- On-device AI components have zero communication paths to restic, key managers, or privileged brokers.
- Privileged Windows brokers possess zero network connectivity and zero storage credentials.
- `Fortiq.Recover` operates completely independently of the Windows background service.
- All IPC messages are authenticated on the receiving side via operating system token validation.
