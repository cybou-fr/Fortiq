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

## Run an operator and a managed peer

FORTIQ permits exactly one service node and one Desktop application per operating
system. Run the operator and managed client on separate machines or virtual
machines. Using alternate configuration files, ports, or IPC names does not bypass
this isolation rule.

On the operator machine, copy `examples/operator.toml` to `operator.toml`, then start it:

```bash
cargo run -p fortiq-service -- --config operator.toml
```

On the managed machine or VM, copy the printed operator PeerId into
`authorization.operator_peer_id` in a copy of `examples/managed.toml`, then start
the managed peer:

```bash
cargo run -p fortiq-service -- --config managed.toml
```

Copy the managed peer's printed reachable listen address and dial it from the
operator (a restart preserves both identities):

```bash
cargo run -p fortiq-service -- --config operator.toml \
  --dial /ip4/MANAGED_VM_IP/udp/4002/quic-v1/p2p/12D3KooW_REPLACE_ME
```

The peers authenticate through libp2p, negotiate `/fortiq/hello/1.0`, and print the remote PeerId and metadata. Press Ctrl+C for clean shutdown.

## Remote shell & Terminal Streaming

First, the user of the managed peer opens a local support ticket:

```bash
cargo run -p fortiq-service -- --config managed.toml ticket open
cargo run -p fortiq-service -- --config managed.toml ticket status
```

After the managed peer is listening, the operator can open an interactive shell:

```bash
cargo run -p fortiq-service -- --config operator.toml \
  --dial /ip4/127.0.0.1/udp/4002/quic-v1/p2p/12D3KooW_TARGET \
  --shell 12D3KooW_TARGET
```

For a non-interactive smoke test, add `--command 'whoami; hostname; uname -a; pwd'`. Managed peers refuse the `--shell` option locally, and the receiving peer rejects every authenticated PeerId except its configured operator. Only one concurrent shell session is permitted per managed peer.

The operator closes the managed peer's ticket over the authenticated P2P connection:

```bash
cargo run -p fortiq-service -- --config operator.toml ticket close \
  --peer 12D3KooW_TARGET \
  --dial /ip4/127.0.0.1/udp/4002/quic-v1/p2p/12D3KooW_TARGET
```

If an active shell session is currently open, `ticket close` is rejected. The workflow is `exit shell -> ticket close -> CLOSED`. After closure, new shell streams are rejected. By default, the ticket is stored beside the identity as `<identity-name>.ticket.json`; `[ticket] path = "..."` overrides that location.

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

Identity files contain private keys and are ignored by Git. On Unix they are created with mode `0600`. Never copy an identity file between machines or expose its contents.
