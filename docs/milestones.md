# Milestones

## Implemented

- Milestone 0: Cargo workspace, documentation, and quality commands.
- Milestone 1: generated-once, persisted libp2p identity; Unix mode `0600`.
- Milestone 2: configuration loading and visible operator/managed mode detection.
- Milestone 3: QUIC listen/dial, Identify, Ping, `/fortiq/hello/1.0`, authenticated remote PeerId reporting, and Ctrl+C shutdown.
- Milestone 4: receiving-peer authorization helper that accepts only the configured operator's authenticated libp2p PeerId.
- Milestone 5: authorized `/fortiq/shell/1.0` bidirectional streams, Linux `/bin/bash` → `/bin/sh` selection, metadata banner, pipe bridging, and child cleanup.
- Milestone 6: persistent `OPEN`/`CLOSED` tickets, local open/status commands, authenticated operator-only remote close, and ticket-gated shell admission.
- Milestone 7: native Windows shell serving with `pwsh.exe` → `powershell.exe` → `cmd.exe` selection and the shared process cleanup path.
- Milestone 8: optional rendezvous server capability plus client registration/discovery in the private `fortiq` namespace.
- Milestone 9: optional Circuit Relay v2 service, client reservations, relayed dialing, and shell operation through a relay.
- Milestone 10: automatic DCUtR direct-upgrade attempts on relayed connections with explicit, non-disruptive relay fallback.
- Milestone 11: Local daemon IPC server (Windows Named Pipes, Unix Domain Sockets) and dual-mode Tauri desktop client (`fortiq-desktop`) with strict Thin Client / Fat Daemon separation, live P2P swarm commands, honest UI states, and real remote ticket closure.
- Milestone 12: Windows ConPTY and Unix PTY terminal integration (`portable-pty`) with binary framing (`ShellFrame`), dedicated terminal IPC pipe/socket forwarding, and frontend `@xterm/xterm` stream handling in Tauri.
- Milestone 13: System service installation/packaging (`systemd` unit on Linux, Windows Service Control Manager wrapper via `windows-service`) and end-to-end multi-platform automated validation tests (`e2e_two_nodes`).

## WSL to VPS smoke test

1. Build the same revision on WSL and the VPS.
2. Start the managed VPS with UDP port 4001 allowed by its host firewall and cloud firewall.
3. Note its printed `/ip4/.../udp/4001/quic-v1/p2p/<PeerId>` address. Replace `0.0.0.0` with the VPS public IP when dialing.
4. Start the WSL operator with `--dial <VPS multiaddress>`.
5. Confirm both processes report the authenticated remote PeerId and HELLO metadata.
6. Restart each peer and confirm its PeerId does not change.

## Planned Next

- Production packaging (.msi / .deb installers) and automated CI/CD staging validation across cloud VPS and WSL instances.

## Explicitly deferred

- Helpdesk bloat: in-app chat, file attachments, ticket categorization/queues, and operator profiles are deferred from the MVP scope.
- PKI, enterprise RBAC, and post-quantum cryptography.
