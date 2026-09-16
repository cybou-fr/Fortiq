# Architecture

Every node runs the same `fortiq-service` binary and holds a persistent Ed25519 libp2p identity. There are no server, agent, or relay-specific applications.

The local administrative mode is derived from configuration:

- missing `authorization.operator_peer_id` → `OPERATOR`;
- present `authorization.operator_peer_id` → `MANAGED`.

The current implementation has three small packages:

- `fortiq-core`: configuration, mode derivation, and HELLO metadata;
- `fortiq-p2p`: identity persistence, the libp2p QUIC swarm, and stream routing;
- `fortiq-shell`: Linux/Windows shell selection, process lifecycle, and stream/pipe bridging;
- `fortiq-service`: startup, visible mode reporting, and CLI arguments.

Milestones 0–10 use direct QUIC connections with optional rendezvous discovery, Circuit Relay v2 fallback, and DCUtR upgrade attempts. PTY/ConPTY remains outside the implemented scope.

Administrative authorization is local to the receiving managed peer. `is_authorized_operator` accepts only the authenticated libp2p remote PeerId and compares it with the configured `authorization.operator_peer_id`. It always denies on an operator-mode node because that node has no configured remote operator.

Each managed peer persists one minimal ticket containing an ID and `OPEN`/`CLOSED` state. Opening is a local support action. Closing is an authenticated `/fortiq/ticket/1.0` request accepted only from the configured operator. Shell admission reads the current persisted state and requires both authorization conditions. To prevent race conditions and ensure that a closed ticket means zero active access without forcibly terminating running tasks, the managed node permits only one concurrent shell session and rejects ticket close requests while that session is active (`exit shell -> ticket close -> CLOSED`).

Network and protocol validation errors (such as mismatched claimed PeerId in HELLO or ticket persistence issues) are isolated: they log warnings and reject the offending request, but never terminate the swarm event loop.

Nodes can configure an explicit `network.public_addr` to avoid advertising internal, loopback, or WSL-private addresses to peers and rendezvous. When an operator node discovers the requested target peer via rendezvous, it automatically initiates dialing to the discovered addresses.

Rendezvous is an optional networking capability independent from operator/managed mode. A capable peer serves the standard libp2p rendezvous protocol; connected peers register and discover within the `fortiq` namespace. No DHT or public IPFS network is involved.

Circuit Relay v2 is another independent capability. A managed or operator peer can provide relay service; peers behind restrictive networking create a reservation through their configured `network.relay_peer`. Higher-level protocols use the same authenticated libp2p connection regardless of whether its transport is direct QUIC or relayed.

DCUtR runs on relayed end-to-end connections and exchanges observed direct addresses through the relay. If simultaneous QUIC dialing succeeds, the swarm gains a direct connection. If it fails after bounded attempts, the relayed connection is retained; shell and ticket layers do not branch on transport type.

## Desktop and Local IPC Architecture

The desktop graphical interface (`fortiq-desktop`) follows a strict **Thin Client / Fat Daemon** separation:

- `fortiq-service` is the single authority for libp2p networking, identity key persistence, QUIC connections, relay reservations, tickets, and shell process spawning. It can execute as a background service (Windows Service under SYSTEM/administrator or Linux systemd service).
- `fortiq-desktop` (Tauri v2) contains no libp2p network stack and executes no administrative shells directly. It communicates with the local `fortiq-service` daemon exclusively via local inter-process communication (IPC):
  - **Windows**: Named Pipe (`\\.\pipe\fortiq-ipc`)
  - **Linux**: Unix Domain Socket (`/run/fortiq.sock` or user runtime directory)
- Closing, minimizing to tray, or restarting the desktop UI never drops P2P swarm connections, relay reservations, or active ticket state.

### Adaptive Interface Modes

The desktop UI queries the local node mode from `fortiq-service` over IPC and adapts its layout:

1. **Operator Console (`OPERATOR` mode)**:
   - Three-panel layout:
     - **Navigation Bar**: Quick access to Tickets, Discovered Peers, Settings, and local operator status.
     - **Ticket & Peer Queue**: List of known/discovered peers, active tickets (`OPEN` / `CLOSED`), and connectivity status (Direct / Relayed / DCUtR).
     - **Session Card**: Technical metadata of the selected peer (hostname, PeerId, OS, detected shell, connection state) with `[Connect]` and `[Close ticket]` action buttons (`Close ticket` is enabled only when no shell session is active).
     - **Embedded Terminal**: Live terminal emulator (`xterm.js`) connected to the remote shell stream over IPC.
   - Non-essential helpdesk bloat (chat, file attachments, category queues, operator profiles) is explicitly excluded from the MVP.

2. **Managed Client (`MANAGED` mode)**:
   - Minimalist user card:
     - Service health: `● Service online`.
     - Request action: `[ OPEN SUPPORT TICKET ]`.
     - Ticket state: Shows ticket ID, `OPEN`, `Operator: <PeerId>`, and status `Waiting for operator...`.
     - Active session: Changes indicator to `● Support connected` when the operator opens the shell stream. No disruptive mid-session disconnect buttons.
