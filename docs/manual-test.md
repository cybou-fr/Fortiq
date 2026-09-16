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

### Option A: Install via Release Bundle

```powershell
# 4. Extract release archive
Expand-Archive -Path FORTIQ-0.1.0-Windows-x64.zip -DestinationPath C:\Temp\FortiqInstall

# 5. Run automated installer as Administrator
powershell -ExecutionPolicy Bypass -File C:\Temp\FortiqInstall\install.ps1
```

The installer:
- Copies binaries to `C:\Program Files\FORTIQ\`
- Adds `C:\Program Files\FORTIQ` to system `PATH`
- Initializes configuration at `C:\ProgramData\FORTIQ\fortiq.toml` with the pre-configured OVH relay
- Installs and starts the background Windows Service (`FortiqService`)
- Creates Start Menu shortcuts and launches the `fortiq-desktop` GUI in the system tray

### Option B: Check Identity and Open Support Ticket

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

On the operator/admin machine (Windows or Linux):

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
# 17. Run uninstaller as Administrator
powershell -ExecutionPolicy Bypass -File "C:\Program Files\FORTIQ\uninstall.ps1"

# 18. Verify service has been stopped and removed
Get-Service FortiqService -ErrorAction SilentlyContinue
```

---

## Troubleshooting Checklist

| Issue | Cause | Fix |
|-------|-------|-----|
| `fortiq: command not found` | PATH not refreshed | Restart shell or run `C:\Program Files\FORTIQ\fortiq.exe` directly |
| `Cannot connect to FORTIQ daemon` | `fortiq-service` not running | Run `Get-Service FortiqService` or `Start-Service FortiqService` |
| Peer not showing in `fortiq peers` | Rendezvous not connected | Check firewall for UDP 4001 outgoing, check `fortiq status` |
| `Access denied: ticket is CLOSED` | Support ticket closed | Run `fortiq ticket open` on the managed node |
