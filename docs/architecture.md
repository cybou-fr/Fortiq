# Architecture

Every node runs the same `fortiq-service` binary and holds a persistent Ed25519 libp2p identity. There are no server, agent, or relay-specific applications.

The local administrative mode is derived from configuration:

- missing `authorization.operator_peer_id` → `OPERATOR`;
- present `authorization.operator_peer_id` → `MANAGED`.

The current implementation has three small packages:

- `fortiq-core`: configuration, mode derivation, and HELLO metadata;
- `fortiq-p2p`: identity persistence and the libp2p QUIC swarm;
- `fortiq-service`: startup, visible mode reporting, and CLI arguments.

Milestones 0–3 use direct QUIC connections. Discovery, relay, DCUtR, tickets, and shell access remain outside the implemented scope.

Administrative authorization is local to the receiving managed peer. `is_authorized_operator` accepts only the authenticated libp2p remote PeerId and compares it with the configured `authorization.operator_peer_id`. It always denies on an operator-mode node because that node has no configured remote operator.
