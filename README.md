# FORTIQ

FORTIQ is a minimal peer-to-peer remote administration system in Rust. Every machine runs the same `fortiq-service`; milestones 0–10 provide persistent identity, configuration-derived operator/managed mode, direct and Circuit Relay v2 connectivity with DCUtR upgrade attempts, rendezvous discovery, HELLO metadata exchange, operator PeerId authorization, persistent support tickets, and Linux/Windows shell prototypes.

Shell authorization requires both an open local ticket and an exact match with the configured operator PeerId.

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

## Remote shell

First, the user of the managed peer opens a local support ticket:

```bash
cargo run -p fortiq-service -- --config managed.toml ticket open
cargo run -p fortiq-service -- --config managed.toml ticket status
```

After the managed peer is listening, the operator can open a pipe-based interactive shell:

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

This milestone uses ordinary pipes, not a PTY. Full-screen terminal applications and job-control behavior are therefore deferred.

On Windows, the managed peer selects `pwsh.exe`, then `powershell.exe`, then `cmd.exe`. PowerShell is launched with `-NoLogo -NoProfile`; cmd uses `/Q`. Linux selects `/bin/bash`, then `/bin/sh`.

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

Identity files contain private keys and are ignored by Git. On Unix they are created with mode `0600`. Never copy an identity file between machines or expose its contents.
