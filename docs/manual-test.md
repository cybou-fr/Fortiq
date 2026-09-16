# FORTIQ Manual Verification Guide

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

The wizard requires the Operator PeerId and refuses to install when it is
missing or invalid. It uses the Windows computer name by default.

The complete installer:
- Copies binaries to `C:\Program Files\FORTIQ\`
- Adds `C:\Program Files\FORTIQ` to system `PATH`
- Writes an explicit Client configuration with the supplied `operator_peer_id`
- Installs and starts the background Windows Service (`FortiqService`)
- Creates Start Menu shortcuts
- Registers `fortiq-desktop.exe` for automatic start at interactive user logon

### Check Identity and Open Support Ticket

Open PowerShell or Command Prompt:

```powershell
# 6. Check daemon connection and local PeerId
fortiq status
fortiq id

# 7. Open support ticket (allowing remote access)
fortiq ticket open

# 8. Confirm ticket state is OPEN
fortiq ticket status
```

*(Alternatively: right-click the FORTIQ tray icon in the taskbar notification area and click **Open Support Ticket**).*

---

## 3. Operator Machine Setup & Remote Administration

On Windows, first install:

```text
FORTIQ-Operator-Setup-<version>-x64.exe
```

The Operator package never writes an `operator_peer_id`. After installation:

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
fortiq shell <MANAGED_PEER_ID>
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
fortiq shell <MANAGED_PEER_ID> --command "Get-Service FortiqService"
```

---

## 4. Closing the Ticket & Authorization Verification

```powershell
# 14. Operator closes the ticket remotely
fortiq ticket close <MANAGED_PEER_ID>
```

### Verify Access is Revoked

Back on the managed machine (or from the operator):

```powershell
# 15. Check ticket state on managed machine (must show CLOSED)
fortiq ticket status

# 16. Attempt to connect again as operator (must be denied)
fortiq shell <MANAGED_PEER_ID>
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
| `Access denied: ticket is CLOSED` | Support ticket closed | Run `fortiq ticket open` on the managed node |

---

## 6. Operator/client isolation

Only one FORTIQ service node and one FORTIQ Desktop application may run on an
operating system. For a complete manual workflow, use this host for the operator
and a separate Windows 11 VM for the managed client. Install the managed package
inside the VM, authorize the operator PeerId, open a ticket there, and perform the
terminal workflow from the host operator console.
