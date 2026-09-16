# FORTIQ

FORTIQ is a minimal peer-to-peer remote administration system in Rust. Every machine runs the same `fortiq-service`; milestones 0–4 provide persistent identity, configuration-derived operator/managed mode, QUIC connectivity, `/fortiq/hello/1.0` metadata exchange, and operator PeerId authorization.

Remote shell, tickets, rendezvous, relay, and DCUtR are intentionally not implemented yet. The authorization helper is ready for the shell protocol, but no administrative stream exists at this milestone.

## Prerequisites

- Rust stable (1.81 or newer)
- UDP connectivity between peers

## Build and verify

```bash
cargo build --workspace
cargo test --workspace
cargo fmt --check
cargo clippy --workspace --all-targets -- -D warnings
```

## Run two local peers

Copy `examples/operator.toml` to `operator.toml`, then start it:

```bash
cargo run -p fortiq-service -- --config operator.toml
```

Copy the printed operator PeerId into `authorization.operator_peer_id` in a copy of `examples/managed.toml`, then start the managed peer:

```bash
cargo run -p fortiq-service -- --config managed.toml
```

Copy the managed peer's printed listen address and dial it from the operator (a restart preserves both identities):

```bash
cargo run -p fortiq-service -- --config operator.toml \
  --dial /ip4/127.0.0.1/udp/4002/quic-v1/p2p/12D3KooW_REPLACE_ME
```

The peers authenticate through libp2p, negotiate `/fortiq/hello/1.0`, and print the remote PeerId and metadata. Press Ctrl+C for clean shutdown.

For WSL/VPS instructions, see [docs/milestones.md](docs/milestones.md).

## Security scope

Identity files contain private keys and are ignored by Git. On Unix they are created with mode `0600`. Never copy an identity file between machines or expose its contents.
