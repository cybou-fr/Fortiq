<p align="center">
  <img src="docs/assets/logo.png" alt="FORTIQ" width="120">
</p>

<h1 align="center">FORTIQ</h1>

<p align="center"><em>Sovereign peer-to-peer remote administration and supervision</em></p>

---

## Who this is for

FORTIQ is built for the clients of our managed support service. It is
distributed and operated by us, for them.

**The source is published for analysis, review, control and teaching** — so that
anyone can audit what runs on a supervised machine, and so that the protocol and
its authorization model can be studied. That is the purpose of this repository.

**Do not use it as a finished product if you are not our client.** Private or
third-party use requires modification: the code assumes our relay and rendezvous
infrastructure, our operator identity, and our operating procedures. Nothing
here ships as a turn-key remote administration suite, there is no public support
channel, and no fitness for any particular deployment is claimed.

If you want FORTIQ running for your own organization, talk to us about becoming
a client, or fork it and take ownership of the changes.

---

FORTIQ is a minimal peer-to-peer remote administration system in Rust. Every machine runs the same `fortiq-service`; milestones 0–13 provide persistent identity, configuration-derived operator/managed mode, direct and Circuit Relay v2 connectivity with DCUtR upgrade attempts, rendezvous discovery, HELLO metadata exchange, operator PeerId authorization, persistent support tickets, native Windows ConPTY and Linux PTY terminal streaming with xterm.js, local IPC, and background system service packaging (Windows Service / systemd).

Shell authorization requires both an open local ticket and an exact match with the configured operator PeerId.

## Prerequisites

- Rust stable (1.81 or newer)
- Node.js (v18+ for building the desktop GUI frontend)
- UDP connectivity between peers

## Build and verify

```bash
cargo build --workspace
cargo test --workspace
cargo fmt --check
cargo clippy --workspace --all-targets -- -D warnings
```

## Quickstart: Operator and Managed Peer

FORTIQ permits exactly one service node and one Desktop application per operating
system. Run the operator and managed client on separate machines or virtual
machines.

### 1. Start the Operator Daemon

On the operator machine, start the background service daemon:

```bash
cargo run -p fortiq-service -- --config operator.toml
```

Note the printed `Local PeerId` (e.g. `12D3KooW_OPERATOR_PEER_ID`).

### 2. Start the Managed Client Daemon

On the managed machine, set `authorization.operator_peer_id = "12D3KooW_OPERATOR_PEER_ID"` in `managed.toml`, then start the client daemon:

```bash
cargo run -p fortiq-service -- --config managed.toml
```

The managed peer connects to the public relay/rendezvous point and advertises its availability.

### 3. Manage via Thin-Client CLI (`fortiq`)

From another shell on the operator machine, use the `fortiq` CLI (which connects via local IPC to the running `fortiq-service`):

```bash
# Verify local operator service status
cargo run -p fortiq -- status

# Discover available managed peers registered on the relay/rendezvous
cargo run -p fortiq -- peers

# Open an interactive remote shell session to the managed peer
cargo run -p fortiq -- shell 12D3KooW_MANAGED_PEER_ID
```

The peers authenticate through mutual libp2p cryptographic handshake (`/fortiq/hello/1.0`), and the interactive ConPTY/PTY shell stream connects over the secure P2P transport.


## Architecture Overview

FORTIQ follows a strict daemon / control-client architecture:
- **`fortiq-service`**: Sovereign background daemon (systemd service on Linux, Windows Service on Windows). Manages P2P QUIC / Relay transports, identity keys, local support tickets, pseudoterminal allocation (ConPTY / PTY), and serves local IPC.
- **`fortiq`**: Lightweight command-line client communicating with `fortiq-service` via local IPC (UNIX domain socket on Linux, Named Pipe on Windows).
- **`fortiq-desktop`**: Desktop GUI console with system tray integration and embedded xterm.js terminal emulator.

FORTIQ permits exactly one service instance per operating system.

## Remote shell & Terminal Streaming

Once the background service (`fortiq-service`) is running:

1. The managed peer's user opens a local support ticket:

```bash
fortiq ticket open
fortiq ticket status
```

2. The operator checks discovered peers and connects to the managed peer:

```bash
# List discovered peers and connection status
fortiq peers

# Open an interactive terminal session
fortiq shell 12D3KooW_TARGET

# Or execute a single non-interactive command
fortiq shell 12D3KooW_TARGET --command "whoami; hostname; uptime"
```

Only the configured operator PeerId is authorized to open terminal sessions. Only one concurrent shell session is permitted per managed peer.

3. The operator closes the managed peer's support ticket when intervention is complete:

```bash
fortiq ticket close 12D3KooW_TARGET
```

If an active shell session is currently open, `ticket close` is rejected by the server (`cannot close ticket while shell session is active`). The contract is `exit shell -> ticket close -> CLOSED`. After closure, new shell streams are immediately rejected. By default, tickets are stored at `C:\ProgramData\FORTIQ\ticket.json` on Windows and `/var/lib/fortiq/ticket.json` on Linux.

A node whose own config sets `[ticket] auto_open = true` opens its ticket when
the service starts, surviving restarts and reboots. It is intended for lab and
infrastructure nodes, defaults to `false`, and is ignored on operator nodes,
which hold no ticket. No remote peer can set it: on a client machine the ticket
stays the user's own consent gesture.

```toml
[ticket]
path = "/var/lib/fortiq/ticket.json"
auto_open = true
```

FORTIQ utilizes native pseudoterminal allocation (`portable-pty` with ConPTY on Windows, openpty on Linux) with binary framing (`ShellFrame`) for interactive terminal sessions, supporting dynamic resizing and full-screen terminal applications.

On Windows, the managed peer selects `pwsh.exe`, then `powershell.exe`, then `cmd.exe`. PowerShell is launched with `-NoLogo -NoProfile`; cmd uses `/Q`. Linux selects `/bin/bash`, then `/bin/sh`.

## System Service (Windows Service & systemd)

`fortiq-service` can be installed and managed as an operating system background service:

```bash
# Install as a system service
fortiq-service service install [--config <path>]

# Service controls
fortiq-service service start
fortiq-service service stop
fortiq-service service status
fortiq-service service uninstall
```

- **Windows**: Managed via the native Service Control Manager (`FortiqService`) and runs using `windows-service`.
- **Linux**: Installs and controls `/etc/systemd/system/fortiq.service` via `systemctl`.

## Desktop Client (`fortiq-desktop`)

FORTIQ includes a dual-mode Tauri desktop client (Operator Console and Managed Client):

```bash
# Build desktop frontend
cd apps/fortiq-desktop
npm run build

# Run desktop application
cargo run --manifest-path apps/fortiq-desktop/src-tauri/Cargo.toml
```

## Production Packaging & Distribution

### Debian / Ubuntu (`.deb`)

Build the standalone system service Debian package:

```bash
# Builds target/debian/fortiq-service_0.1.0_amd64.deb
./packaging/linux/build_deb.sh [version] [target]
```

Install and manage:

```bash
sudo dpkg -i fortiq-service_0.1.0_amd64.deb
sudo nano /etc/fortiq/fortiq.toml
sudo systemctl start fortiq
sudo systemctl status fortiq
```

### Windows Product Installers

The Windows release produces two complete, role-specific products. Both contain
the service, IPC CLI, and desktop/tray application:

```text
FORTIQ-Operator-Setup-<version>-x64.exe
FORTIQ-Client-Setup-<version>-x64.exe
```

Client Setup requires an Operator PeerId and cannot silently fall back to the
Operator role. Both installers use the Windows computer name, install the
service for boot startup, and register the desktop application for user logon.

Build both installers locally with NSIS installed:

```powershell
./scripts/build_windows_dist.ps1 -Version 0.1.0-beta
```

### Multi-Platform Desktop Bundles

The desktop GUI and background service are bundled for production via Tauri:

```bash
cd apps/fortiq-desktop
npm run tauri build
```

- **Windows**: Use the role-specific complete product installers above; the standalone Tauri bundle is not a complete FORTIQ installation.
- **Linux**: Produces `.deb` and `.AppImage` bundles under `target/release/bundle/`.
- **GitHub Actions**: Tagging a commit (`git tag v0.1.0 && git push origin v0.1.0`) triggers `.github/workflows/release.yml`, automatically building and attaching all Linux `.deb`, Windows `.zip`, and desktop installer artifacts to the GitHub Release.


## Rendezvous discovery

Any ordinary FORTIQ peer can provide rendezvous by setting:

```toml
[network]
# Optional explicit public address for VPS/public nodes:
public_addr = "/ip4/203.0.113.10/udp/4001/quic-v1"

[capabilities]
rendezvous = true
relay = false
```

Peers dial it with the existing `--dial <multiaddr>` option. They register their authenticated peer record and current listen addresses in the `fortiq` namespace, then discover existing registrations. If an operator requests a `--shell <TARGET_PEER>`, discovering that target via rendezvous automatically initiates dialing its discovered address. Rendezvous capability does not grant administrative authority.

## Circuit Relay v2

An ordinary peer enables relay service with `[capabilities] relay = true`. A peer that needs an inbound relay reservation configures the relay's direct address:

```toml
[network]
relay_peer = "/ip4/203.0.113.10/udp/4001/quic-v1/p2p/12D3KooW_RELAY"
```

Its relayed address is then:

```text
/ip4/203.0.113.10/udp/4001/quic-v1/p2p/12D3KooW_RELAY/p2p-circuit/p2p/12D3KooW_TARGET
```

Pass that address to `--dial`. HELLO, ticket administration, and shell streams work unchanged over the circuit.

When end peers establish a relayed connection, DCUtR automatically attempts a direct QUIC hole punch using observed addresses exchanged over the circuit. Success and failure are reported explicitly. A failed upgrade does not close the relay connection, so existing and future protocol streams continue through the circuit.

For WSL/VPS instructions, see [docs/milestones.md](docs/milestones.md).

## Security scope

The ticket is the supervised user's consent gesture. Only that user, on their own
machine, can open one; the operator can never open a ticket remotely. A shell is
accepted only while a ticket is open and only from the exactly configured
operator PeerId, authenticated by libp2p. Closing a ticket is the operator's
action, so a client cannot interrupt an operation in progress.

`[ticket] auto_open = true` lets a machine open its own ticket at service start.
It is meant for lab and infrastructure nodes that must stay reachable across
restarts, it is set only in that machine's own local config, and no remote peer
can turn it on. A real client machine leaves it off.

Transport is authenticated and encrypted: QUIC for direct links, Noise + Yamux
for relay circuits, so a relay never sees plaintext. There is no separate
per-session key for a ticket, chat or shell today, so sessions are not
cryptographically isolated from one another beyond the transport.

Identity files contain private keys and are ignored by Git. On Unix they are created with mode `0600`. On Windows, `C:\ProgramData\FORTIQ` is strictly ACL-hardened to `SYSTEM` (`*S-1-5-18`) and `Administrators` (`*S-1-5-32-544`), blocking standard unprivileged user accounts from reading private key material. Never copy an identity file between machines or expose its contents.

## Clean-Machine Install & Lifecycle Flow

1. **Packaging & Deployment:**
   - **Windows:** Run `FORTIQ-Client-Setup-<version>-x64.exe` (with required operator PeerId) or `FORTIQ-Operator-Setup-<version>-x64.exe`.
   - **Linux:** Install `fortiq-service_<version>_amd64.deb` and start via `systemctl start fortiq`.
2. **Initial Service Startup:**
   - On first launch, the daemon inspects `[identity] path`. If absent, a new Ed25519 keypair is cryptographically generated and safely saved with restricted permissions.
   - The daemon connects to the configured relay node, reserves a circuit slot, and registers its authenticated circuit address on the rendezvous point.
3. **Session Lifecycle:**
   - The managed user opens a ticket via GUI or `fortiq ticket open`.
   - The operator discovers the peer via Rendezvous, inspects metadata, and connects over the Relay circuit.
   - Interactive shell sessions run through native PTY/ConPTY streaming. Abandoned sessions automatically release after 60 seconds of inactivity.
   - Intervention ends with `exit` in the shell followed by `fortiq ticket close`.

## Repository Governance & Branch Protection

To ensure unbroken stability across Linux and Windows targets, the following GitHub branch protection rules are recommended for `main`:
- **Require a pull request before merging:** Require at least 1 approving review.
- **Require status checks to pass before merging:**
  - `Code Formatting` (`cargo fmt --check`)
  - `Workspace Tests & Clippy (ubuntu-latest)`
  - `Workspace Tests & Clippy (windows-latest)`
  - `Tauri Desktop Frontend & Backend Check (ubuntu-latest)`
  - `Tauri Desktop Frontend & Backend Check (windows-latest)`
- **Require linear history:** Enforce rebase or squash merges to maintain a clean git trajectory.

