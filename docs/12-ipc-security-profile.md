# Local IPC Protocol & Security Profile

> **Implementation status: Implemented.** `PasswordPipeServer` performs the described process, account and signature checks.


## Scope & Operational Context

This document defines the local Inter-Process Communication (IPC) architecture for:
- Desktop UI (`Fortiq.Desktop`) → Background Service (`Fortiq.Service`);
- Background Service → Privileged Windows Platform Broker (`Fortiq.Platform.Windows`);
- Repository Engine (`restic`) → Ephemeral Password Helper (`Fortiq.PasswordHelper`).

---

## Named Pipe Endpoint Addressing

```text
\\.\pipe\fortiq-password-v1-<operation-id-hex>
```

The pipe name format is `fortiq-password-v1-{id:N}` generated strictly from the operation's canonical GUID (`Guid.NewGuid()`). Arbitrary user inputs, file paths, and repository names are strictly prohibited in pipe names.

---

## Operating System Protection Layers

Every server-side named pipe instance:
- Explicitly declares an access control security descriptor (DACL), strictly disallowing default DACL inheritance;
- Enables `PIPE_REJECT_REMOTE_CLIENTS` to prevent network-based named pipe exploitation;
- Enforces `FILE_FLAG_FIRST_PIPE_INSTANCE` on the server listener to prevent pre-creation squatting attacks;
- Defines restrictive read/write access rights and bounded buffer capacities;
- Utilizes asynchronous overlapped I/O with absolute cancellation deadlines;
- Verifies client process identity and account SID before parsing message payloads.

---

## Security Identity Matrix

| Endpoint | Server Host | Authorized Client | Access Control Principle |
| :--- | :--- | :--- | :--- |
| `fortiq-password-v1-<id>` | Service or Recover process | Specific child helper process (`Fortiq.PasswordHelper`) | One instance, single client read, immediate closure and zeroization. |

---

## Client Authentication Handshake (`Fortiq.PasswordHelper`)

Implemented in `PasswordPipeServer.cs`, `PasswordPipeProtocol`, and `NamedPipeClientPeer.cs`:

1. **Client Connection**: When the external engine invokes `--password-command "Fortiq.PasswordHelper.exe <operation-id>"`, the child helper connects to `\\.\pipe\fortiq-password-v1-{id:N}`.
2. **Binary Challenge-Response**:
   - Server sends a cryptographically random 32-byte binary challenge.
   - Client computes `SHA256("fortiq/password-helper/v1" || challenge)` and returns the 32-byte response.
   - Server verifies the response in constant time.
3. **Process Resolution**: The server resolves the client's process ID via `GetNamedPipeClientProcessId`.
4. **Handle Pinning & Image Matching**: The server opens the process handle (`QueryFullProcessImageName`) and compares the client's image path against the verified, open file handle of `Fortiq.PasswordHelper.exe` (`PinnedFile`) to eliminate executable replacement attacks.
5. **Authenticode Digital Signature**: In environments with `RequireSignedHelper = true`, `AuthenticodeSignature` confirms Microsoft Authenticode trust.
6. **Security Impersonation Check**: The server invokes `ImpersonateNamedPipeClient` to verify the client process token and account SID (`WindowsIdentity.GetCurrent().User`), immediately followed by `RevertToSelf` in a guaranteed `finally` block.
7. **Single-Use Delivery & Memory Zeroization**: The Engine Unlock Secret (encoded via `EnginePasswordV1Encoder` followed by `\n`) is transmitted across the pipe once, followed by immediate pipe termination and cryptographic memory zeroization (`CryptographicOperations.ZeroMemory`).

---

## Mandatory Security Test Assertions

1. Remote network pipe connections are rejected by the OS kernel.
2. Unprivileged or anonymous users cannot open internal service pipes.
3. Substituted binaries or mismatched PIDs are rejected immediately before transmitting credentials.
4. Pre-created squatting pipes cause immediate service startup failure rather than connecting to hostile listeners.
5. `RevertToSelf` executes under all failure, exception, and cancellation conditions.
6. Ephemeral secret pipes self-destruct upon timeout or completion of the single read.
