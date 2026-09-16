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

Each managed peer persists one minimal ticket containing an ID and `OPEN`/`CLOSED` state. Opening is a local support action. Closing is an authenticated `/fortiq/ticket/1.0` request accepted only from the configured operator. Shell admission reads the current persisted state and requires both authorization conditions.

Rendezvous is an optional networking capability independent from operator/managed mode. A capable peer serves the standard libp2p rendezvous protocol; connected peers register and discover within the `fortiq` namespace. No DHT or public IPFS network is involved.

Circuit Relay v2 is another independent capability. A managed or operator peer can provide relay service; peers behind restrictive networking create a reservation through their configured `network.relay_peer`. Higher-level protocols use the same authenticated libp2p connection regardless of whether its transport is direct QUIC or relayed.

DCUtR runs on relayed end-to-end connections and exchanges observed direct addresses through the relay. If simultaneous QUIC dialing succeeds, the swarm gains a direct connection. If it fails after bounded attempts, the relayed connection is retained; shell and ticket layers do not branch on transport type.
