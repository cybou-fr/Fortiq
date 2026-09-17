<p align="center">
  <img src="docs/assets/logo.png" alt="FORTIQ" width="120">
</p>

<h1 align="center">FORTIQ</h1>

<p align="center"><em>Sovereign peer-to-peer support platform</em></p>

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

FORTIQ is a sovereign support network composed of ordinary P2P nodes. Its application state is an immutable signed object graph, encrypted end-to-end with post-quantum algorithms, erasure-coded where appropriate, and distributed across storage-capable peers.

The operator is not a machine. The network owner/operator authority is rooted in Genesis and is portable through a 24-word BIP-39 mnemonic.

```text
Existing libp2p transport (QUIC, Relay v2, Rendezvous, DCUtR)
        ↓
Genesis / Owner Root / Network Policy
        ↓
Entity + Segment membership
        ↓
Immutable logical events
        ↓
EventPacks / Blob Manifests
        ↓
PQ-resistant encryption (FORTIQ-PQ1: ML-KEM-768 hybrid, ML-DSA-65)
        ↓
Ciphertext storage objects
        ↓
Replication or Reed–Solomon shards
        ↓
Anti-entropy / repair / GC
        ↓
Reducers / encrypted snapshots
        ↓
Tickets / Chat / Files / Shell / UI
```

### Hard Invariants

1. **Node PeerId is transport identity only.**
2. **Owner/Admin/Operator authority is rooted in signed Genesis.**
3. **No permanent operator machine exists.**
4. **A 24-word mnemonic unlocks temporary Operator Authority on any FORTIQ node without changing its PeerId.**
5. **Each client has an independent cryptographic Segment.**
6. **The operator uses a different deterministic HPKE recipient key for every client Segment.**
7. **A Segment key leak for Client A MUST NOT decrypt Client B.**
8. **One logical payload is encrypted once; recipients receive independent key envelopes.**
9. **High-volume chat/state is batched into small immutable EventPacks to amortize PQ signature and HPKE overhead.**
10. **Nobody edits an object in place, including Admin.**
11. **Ordinary actors create successor events/versions.**
12. **Admin may publish authoritative presentation overrides and tombstones, but cannot use them to bypass a client's local shell revocation.**
13. **Ticket access is controlled by a client-owned access epoch.**
14. **A client-signed close/revoke invalidates shell access immediately on that client, before distributed convergence.**
15. **Control-plane objects are highly replicated because they are tiny and needed for bootstrapping.**
16. **Large state packs and blobs are encrypted first and Reed–Solomon encoded second.**
17. **Shard integrity is verified independently before RS reconstruction.**
18. **No plaintext operator-wide database is canonical or required.**
19. **Local materialized views are disposable caches.**
20. **Protocol and cryptographic profiles are explicitly versioned and fail closed on downgrade.**

The complete Canonical Architecture v3 specification suite and ADRs are published in [`docs/`](docs/README.md).
See also the [Architecture Specification](docs/architecture.md), [Protocol Specification](docs/protocol.md), [Architecture Decision Records (ADRs)](docs/adr/ADR-001-genesis-owner-signing-only.md), [Detailed Specifications](docs/spec/00-architecture-review.md), and [Implementation Roadmap](docs/milestones.md).


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

Both operator and managed peers connect to the sovereign public relay/rendezvous
infrastructure for NAT traversal and peer discovery:

```toml
[network]
relay_peer = "/ip4/51.255.46.58/udp/4001/quic-v1/p2p/12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy"
```

Template configurations are provided in `examples/operator.toml`, `examples/managed.toml`, and `examples/relay.toml`.

### 1. Start the Operator Daemon

On the operator machine, start the background service daemon:

```bash
cargo run -p fortiq-service -- --config examples/operator.toml
```

Note the printed `Local PeerId` (e.g. `12D3KooW_OPERATOR_PEER_ID`).

### 2. Start the Managed Client Daemon

On the managed machine, set `authorization.operator_peer_id = "12D3KooW_OPERATOR_PEER_ID"` in `examples/managed.toml`, then start the client daemon:

```bash
cargo run -p fortiq-service -- --config examples/managed.toml
```

The managed peer connects to the public relay/rendezvous point and advertises its availability.

### 3. Manage via Thin-Client CLI (`fortiq`)

From another shell on the operator machine, use the `fortiq` CLI (which connects via local IPC to the running `fortiq-service`):

```bash
# Verify local operator service status
cargo run -p fortiq -- status

# Discover available managed peers registered on the relay/rendezvous
cargo run -p fortiq -- peers

# On the managed client, create a ticket (remote shell remains disabled)
cargo run -p fortiq -- ticket create --title "Outlook startup failure" --priority HIGH

# Exchange ticket-scoped chat or files
cargo run -p fortiq -- ticket message FTQ_TICKET_ID "I can reproduce the issue"
cargo run -p fortiq -- ticket send-file FTQ_TICKET_ID ./diagnostics.zip

# The managed user explicitly enables terminal access
cargo run -p fortiq -- ticket access FTQ_TICKET_ID true

# The operator opens a shell bound to that ticket
cargo run -p fortiq -- shell 12D3KooW_MANAGED_PEER_ID --ticket-id FTQ_TICKET_ID
```

The peers authenticate through mutual libp2p cryptographic handshake (`/fortiq/hello/1.0`), and the interactive ConPTY/PTY shell stream connects over the secure P2P transport.


## Architecture Overview

FORTIQ follows a strict daemon / control-client architecture:
- **`fortiq-service`**: Sovereign background daemon (systemd service on Linux, Windows Service on Windows). Manages P2P QUIC / Relay transports, identity keys, local support tickets, pseudoterminal allocation (ConPTY / PTY), and serves local IPC.
- **`fortiq`**: Lightweight command-line client communicating with `fortiq-service` via local IPC (UNIX domain socket on Linux, Named Pipe on Windows).
- **`fortiq-desktop`**: Desktop GUI console with system tray integration and embedded xterm.js terminal emulator.

FORTIQ permits exactly one service instance per operating system.

## Ticket lifecycle and terminal streaming

Once the background service (`fortiq-service`) is running:

1. The managed user creates a ticket. Creating it does not grant shell access:

```bash
fortiq ticket create --title "VPN connection failure" --description "Error 809" --priority HIGH
fortiq ticket list
```

2. Chat and files are available while the ticket permits work. The managed user
   separately enables remote assistance:

```bash
fortiq ticket message FTQ_TICKET_ID "The failure started this morning"
fortiq ticket send-file FTQ_TICKET_ID ./vpn.log
fortiq ticket access FTQ_TICKET_ID true
```

3. The operator checks discovered peers and connects using the exact ticket ID:

```bash
# List discovered peers and connection status
fortiq peers

# Open an interactive terminal session
fortiq shell 12D3KooW_TARGET --ticket-id FTQ_TICKET_ID

# Or execute a single non-interactive command
fortiq shell 12D3KooW_TARGET --ticket-id FTQ_TICKET_ID --command "whoami; hostname; uptime"
```

Only the configured operator PeerId is authorized to open terminal sessions. Only one concurrent shell session is permitted per managed peer. Revoking access terminates an active shell.

4. The operator resolves or closes the ticket through the canonical managed client:

```bash
fortiq ticket set-status FTQ_TICKET_ID RESOLVED
fortiq ticket set-status FTQ_TICKET_ID CLOSED
```

After closure, chat, file transfer, and new shell streams are rejected.
Tickets, messages, attachments, events, and durable outbox records are stored in
a local SQLite database. In accordance with Canonical Architecture v3, local databases
act as disposable materialized view caches that can be purged and completely reconstructed
from verified, immutable encrypted event streams.


A node whose own config sets `[ticket] auto_open = true` opens its ticket when
the service starts, surviving restarts and reboots. It is intended for lab and
infrastructure nodes, defaults to `false`, and is ignored on operator nodes,
which hold no ticket. It creates a normal canonical ticket using the machine's
real PeerId and configured operator PeerId. It still leaves remote shell access
disabled. No remote peer can enable `auto_open`.

```toml
[ticket]
path = "/var/lib/fortiq/tickets.db"
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

## Security scope and guarantees (Canonical v3)

FORTIQ guarantees tenant isolation, post-quantum confidentiality, and sovereign client control through its cryptographic model:

1. **Cryptographic Segmentation:** Every client organization has an independent cryptographic `SegmentId`. The operator uses deterministic, distinct HPKE recipient keys for each Segment. An operator key leak for Client A **MUST NOT** decrypt data belonging to Client B.
2. **Client-Owned Access Epochs:** Only a managed client can create a ticket and generate a `TicketAccessEpoch`. Shell access requires an active ticket, valid `TicketAccessEpoch`, and an unexpired `OperatorSessionCertificate`.
3. **Immediate Synchronous Revocation:** When a client revokes consent or closes a ticket, the managed node immediately writes the revocation event, invalidates the local access epoch, and synchronously kills the running PTY process tree before transmitting the event across the network. Operator administrative overrides cannot revive an invalidated access epoch.
4. **Post-Quantum Cryptography (FORTIQ-PQ1):** Data is protected against harvest-now-decrypt-later attacks via hybrid ML-KEM-768/X25519 HPKE and ML-DSA-65 digital signatures.
5. **Separation of Transport and Authority:** A libp2p `PeerId` represents transport identity only. Holding an active QUIC connection confers zero administrative privilege. Administrative authority is rooted in signed Genesis and portable via a 24-word mnemonic.
6. **Tiered Durability with Integrity Verification:** Large StatePacks and blobs are encrypted first and Reed–Solomon erasure-coded second. Every shard is independently checksummed with `BLAKE3-256` before acceptance for RS reconstruction.
7. **Zero Plaintext Database:** Local SQLite databases and indexes serve solely as disposable materialized view caches. Decrypted operator state is retained in zeroized volatile memory.
8. **Host Permission Hardening:** Identity and seed material are strictly protected. On Unix, identity files use mode `0600`. On Windows, `C:\ProgramData\FORTIQ` is strictly ACL-hardened to `SYSTEM` (`*S-1-5-18`) and `Administrators` (`*S-1-5-32-544`), blocking unprivileged user access. Private keys and seed phrases are never committed, logged, or transmitted.

## Clean-Machine Install & Lifecycle Flow

1. **Packaging & Deployment:**
   - **Windows:** Run `FORTIQ-Client-Setup-<version>-x64.exe` (with required operator PeerId) or `FORTIQ-Operator-Setup-<version>-x64.exe`.
   - **Linux:** Install `fortiq-service_<version>_amd64.deb` and start via `systemctl start fortiq`.
2. **Initial Service Startup:**
   - On first launch, the daemon inspects `[identity] path`. If absent, a new Ed25519 keypair is cryptographically generated and safely saved with restricted permissions.
   - The daemon connects to the configured relay node, reserves a circuit slot, and registers its authenticated circuit address on the rendezvous point.
3. **Session Lifecycle:**
   - The managed user creates a ticket via GUI or `fortiq ticket create`.
   - The operator discovers the peer via Rendezvous, inspects metadata, and connects over the Relay circuit.
   - The managed user explicitly enables remote access for that ticket.
   - Interactive shell sessions run through native PTY/ConPTY streaming. Abandoned sessions automatically release after 60 seconds of inactivity.
   - Intervention ends with `exit`, consent revocation, and a ticket transition to `RESOLVED` or `CLOSED`.

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
