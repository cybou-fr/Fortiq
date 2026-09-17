# FORTIQ Manual Verification Guide

> **Architectural Note:** This guide covers verification of the production transport mesh, background service daemon, local IPC, and packaging (M14/M15 baseline). Application protocol evolution conforms to [Canonical Architecture v3](architecture.md) and the [Canonical Roadmap](milestones.md).

This guide describes how to manually test FORTIQ end-to-end as a testable product without requiring a Rust/Cargo development environment on client machines.

---

## Architecture Topology

```
+--------------------------------------------------------------------+
|                         OVH VPS Relay                              |
|           IP: 51.255.46.58 | Port: 4001 UDP (QUIC)                 |
| PeerId: 12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy       |
+---------------------------------+----------------------------------+
                                  |
            +---------------------+---------------------+
            | (Circuit Relay v2 & Rendezvous)           |
            v                                           v
+-----------------------+                   +------------------------+
|     Managed PC        |                   |     Operator PC        |
|  (User Machine)       |                   |  (Admin / Engineer)    |
| - fortiq-service      |                   | - fortiq-service       |
| - fortiq-desktop(GUI) |                   | - fortiq CLI (or GUI)  |
| - ticket: OPEN        |                   | - authorized PeerId    |
+-----------------------+                   +------------------------+
```

---

## 1. VPS Relay Health Check (OVH Cloud)

Ensure the public relay and rendezvous node is active:

```bash
# 1. Connect to VPS
ssh debian@vps-d0669a91.vps.ovh.net

# 2. Check systemd service status
systemctl status fortiq

# 3. View recent relay/rendezvous logs
journalctl -u fortiq -n 20 --no-pager
```

> **Public Multiaddress:**  
> `/ip4/51.255.46.58/udp/4001/quic-v1/p2p/12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy`

---

## 2. Managed Machine Setup (Windows)

On the managed client machine (where remote support is requested):

### Install the complete Client product

Download and run:

```text
FORTIQ-Client-Setup-<version>-x64.exe
```

The wizard may request the compatibility Operator PeerId for discovery and
ticket routing. This value is not a shell authorization grant. The current
runtime authorizes shell access through `/fortiq/shell/next`, an Owner-signed
operator session certificate, and the client-owned ticket AccessEpoch.

The complete installer:
- Copies binaries to `C:\Program Files\FORTIQ\`
- Adds `C:\Program Files\FORTIQ` to system `PATH`
- Writes the compatibility Client configuration with the supplied
    `operator_peer_id` for discovery and ticket routing
- Installs and starts the background Windows Service (`FortiqService`)
- Creates Start Menu shortcuts
- Registers `fortiq-desktop.exe` for automatic start at interactive user logon

### Check Identity and Create a Support Ticket

Open PowerShell or Command Prompt:

```powershell
# 6. Check daemon connection and local PeerId
fortiq status
fortiq id

# 7. Create a support ticket (remote access remains disabled)
fortiq ticket create --title "Manual acceptance test" --priority NORMAL

# 8. Note the FTQ ticket ID, then explicitly enable terminal access
fortiq ticket list
fortiq ticket access <TICKET_ID> true
```

The same ticket and consent controls are available in the managed Desktop application.

---

## 3. Operator Machine Setup & Remote Administration

On Windows, first install:

```text
FORTIQ-Operator-Setup-<version>-x64.exe
```

The Operator package does not need an `operator_peer_id` for shell authority.
After installation:

```powershell
# 9. Verify operator daemon is running and check its status
fortiq status

# 10. Check list of discovered peers through relay/rendezvous
fortiq peers
```

Note the managed node's PeerId (e.g. `12D3KooW...`).

### Interactive Shell Connection

```powershell
# 11. Connect interactively to remote terminal
fortiq shell <MANAGED_PEER_ID> --ticket-id <TICKET_ID>
```

You are now in an interactive ConPTY (Windows) or PTY (Linux) remote terminal session.

```powershell
# 12. Run inspection commands on the remote machine
whoami
hostname
Get-Process | Select-Object -First 10
```

Type `exit` to detach from the remote shell session.

### Non-Interactive One-Shot Command Execution

```powershell
# 13. Execute a single command remotely and stream output
fortiq shell <MANAGED_PEER_ID> --ticket-id <TICKET_ID> --command "Get-Service FortiqService"
```

---

## 4. Closing the Ticket & Authorization Verification

```powershell
# 14. Operator closes the canonical ticket
fortiq ticket set-status <TICKET_ID> CLOSED
```

### Verify Access is Revoked

Back on the managed machine (or from the operator):

```powershell
# 15. Check ticket state on managed machine (must show CLOSED)
fortiq ticket show <TICKET_ID>

# 16. Attempt to connect again as operator (must be denied)
fortiq shell <MANAGED_PEER_ID> --ticket-id <TICKET_ID>
# Expected output: Connection closed by remote host (Ticket CLOSED / Unauthorized)
```

---

## 5. Uninstallation (Clean Teardown)

When testing is complete on the Windows managed machine:

```powershell
# 17. Use Windows Installed Apps, or run as Administrator
& "C:\Program Files\FORTIQ\Uninstall FORTIQ.exe"

# 18. Verify service and desktop autostart have been removed
Get-Service FortiqService -ErrorAction SilentlyContinue
Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "FORTIQ Desktop" -ErrorAction SilentlyContinue
```

Identity and configuration remain in `C:\ProgramData\FORTIQ` by default.
Removing them requires a separate explicit `uninstall.ps1 -RemoveData` confirmation.

---

## Troubleshooting Checklist

| Issue | Cause | Fix |
|-------|-------|-----|
| `fortiq: command not found` | PATH not refreshed | Restart shell or run `C:\Program Files\FORTIQ\fortiq.exe` directly |
| `Cannot connect to FORTIQ daemon` | `fortiq-service` not running | Run `Get-Service FortiqService` or `Start-Service FortiqService` |
| Peer not showing in `fortiq peers` | Rendezvous not connected | Check firewall for UDP 4001 outgoing, check `fortiq status` |
| `Access denied: ticket is CLOSED` | Support ticket closed | Create a new managed ticket with `fortiq ticket create` |

---

## 6. Operator/client isolation

Only one FORTIQ service node and one FORTIQ Desktop application may run on an
operating system. For a complete manual workflow, use this host for the operator
and a separate Windows 11 VM for the managed client. Install the managed package
inside the VM, authorize the operator PeerId, create a ticket there, and perform the
terminal workflow from the host operator console.
