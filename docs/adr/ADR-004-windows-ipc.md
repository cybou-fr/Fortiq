# ADR-004: Windows Named Pipes for Local IPC

- Status: **Accepted & Implemented for Windows V1**
- Date: **September 3, 2026**
- Scope: Inter-process communication between Desktop, Service, Platform Broker, and Password Helper

---

## Context

Fortiq requires a high-performance, local-only IPC transport on Windows supporting native access control descriptors (DACLs), caller impersonation checks, and least-privilege token verification without opening network TCP sockets.

---

## Decision

Adopt **Windows Named Pipes (`\\.\pipe\fortiq-password-v1-...`)** as the mandatory local transport for password delivery on Windows.

### Key Implementation Specifications:
1. **Local Confinement**: Created strictly with `PIPE_REJECT_REMOTE_CLIENTS`.
2. **Squatting Mitigation**: Enforces `FILE_FLAG_FIRST_PIPE_INSTANCE` on server listeners to detect and fail closed on pre-created listener race conditions.
3. **Specific Account SIDs**: Access granted exclusively to dedicated service SIDs and approved operator tokens rather than generic `Administrators` or `SYSTEM`.
4. **Token & Image Matching**: Server validates client process PID, compares the running image path against held binary file handles (`PinnedFile`), and verifies token integrity.
5. **Single-Use Ephemeral Channels**: Secret delivery (`Fortiq.PasswordHelper`) uses dedicated per-operation pipe names (`\\.\pipe\fortiq-password-v1-{operationId:N}`) destroyed and zeroized immediately upon secret transfer.

---

## Why Not Loopback HTTP / gRPC over TCP?

Loopback TCP introduces port-binding race conditions, firewall interference, proxy configuration traps, and lacks native Windows kernel identity propagation. Named Pipes provide kernel-enforced DACL protection and caller SID resolution without network stack involvement.
