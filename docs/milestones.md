# Milestones

## Implemented

- Milestone 0: Cargo workspace, documentation, and quality commands.
- Milestone 1: generated-once, persisted libp2p identity; Unix mode `0600`.
- Milestone 2: configuration loading and visible operator/managed mode detection.
- Milestone 3: QUIC listen/dial, Identify, Ping, `/fortiq/hello/1.0`, authenticated remote PeerId reporting, and Ctrl+C shutdown.
- Milestone 4: receiving-peer authorization helper that accepts only the configured operator's authenticated libp2p PeerId.

## WSL to VPS smoke test

1. Build the same revision on WSL and the VPS.
2. Start the managed VPS with UDP port 4001 allowed by its host firewall and cloud firewall.
3. Note its printed `/ip4/.../udp/4001/quic-v1/p2p/<PeerId>` address. Replace `0.0.0.0` with the VPS public IP when dialing.
4. Start the WSL operator with `--dial <VPS multiaddress>`.
5. Confirm both processes report the authenticated remote PeerId and HELLO metadata.
6. Restart each peer and confirm its PeerId does not change.

## Explicitly deferred

Milestones 5 and later: remote shell, tickets, Windows shell selection, rendezvous, circuit relay, and DCUtR.
