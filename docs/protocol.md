# Protocol

## Transport and identity

Peers use QUIC through rust-libp2p. The remote PeerId is authenticated by the libp2p secure transport. FORTIQ does not implement custom cryptography or challenge-response.

## HELLO

Protocol ID: `/fortiq/hello/1.0`

After an outbound connection is established, the dialing peer sends a bounded JSON request containing:

- persistent PeerId;
- node name;
- derived mode (`OPERATOR` or `MANAGED`);
- operating system;
- architecture;
- FORTIQ version.

The receiver verifies that the claimed PeerId equals the authenticated connection PeerId and responds with the same metadata shape. The request and response codecs both cap HELLO payloads at 16 KiB; metadata is also validated after decoding.

## Shell

Protocol ID: `/fortiq/shell/1.0`

The receiving peer obtains the remote PeerId from the authenticated libp2p connection and compares it with `authorization.operator_peer_id`. It sends a one-byte authorization status code before any shell data:
- `0x01` (`AUTHORIZED`): Operator is authorized, ticket is open, and no other session is running.
- `0x00` (`DENIED`): Peer is not an authorized operator.
- `0x02` (`DENIED_NO_TICKET`): Peer is authorized, but no ticket is open (client user has not opened a session).
- `0x03` (`DENIED_BUSY`): Peer is authorized and ticket is open, but another shell session is already active.

A denial code closes the stream immediately without launching a process. Only one concurrent shell session is permitted per managed peer; any secondary shell connection attempts while an active session exists return `DENIED_BUSY`.

On an authorized peer with an open ticket and no active shell session, FORTIQ selects the platform shell. Linux uses `/bin/bash` then `/bin/sh`; Windows uses `pwsh.exe`, `powershell.exe`, then `cmd.exe`.
Native pseudoterminal allocation is performed via `portable-pty` (ConPTY on Windows, openpty on Unix). The `/fortiq/shell/1.0` stream is framed using binary `ShellFrame` packets with a 3-byte header (`tag: u8`, `len: u16` big-endian):
- `0x00` (`Data`): standard terminal byte streams with a 16-bit big-endian length prefix (up to 64 KiB payload).
- `0x01` (`Resize`): dynamic window geometry updates (`len = 4`, `cols: u16`, `rows: u16` big-endian) propagated to the ConPTY/PTY master buffer.
- `0x02` (`Ping`): keepalive telemetry sent periodically (every 15s) by the host to verify operator liveness.
- `0x03` (`Pong`): keepalive response sent back to the host.

A 60-second liveness timeout (`LIVENESS_TIMEOUT`) applies to active sessions: if no frame is received from the operator within 60 seconds, the served session automatically closes to avoid stranded processes.
Closing the connection drops the PTY master, terminates the child process tree immediately, and cleans up the session guard.

## Ticket administration

Protocol ID: `/fortiq/ticket/1.0`

The MVP exposes one administrative request: `Close`. The managed receiver compares the authenticated remote PeerId with its configured operator before changing local persistent state. If an active shell session is currently open on the peer, `Close` requests are rejected with an explicit error to prevent race conditions without forcefully killing running processes (`exit shell -> ticket close -> CLOSED`). Unauthorized requests return an error and leave the ticket unchanged. Ticket request and response payloads are capped at 1 KiB and 4 KiB respectively.

## Rendezvous

FORTIQ uses the standard libp2p `/rendezvous/1.0.0` protocol and the namespace `fortiq`. A peer with `capabilities.rendezvous = true` accepts registrations and discovery requests. Clients publish signed peer records containing their active listen addresses. Discovery results retain their signed PeerId/address association and are made available to the swarm for later dialing.

## Circuit Relay

FORTIQ uses libp2p Circuit Relay v2. A peer with `capabilities.relay = true` accepts bounded reservations and circuits using rust-libp2p's default relay limits. A client with `network.relay_peer` requests and renews a reservation and advertises the resulting `/p2p-circuit` address. Noise and Yamux secure/multiplex the relayed transport; the end peers retain their authenticated PeerIds, so shell and ticket authorization rules remain unchanged.

## DCUtR

Relayed end peers negotiate the standard libp2p DCUtR protocol and attempt simultaneous direct QUIC dialing with observed address candidates. FORTIQ logs whether a direct connection was established. Upgrade errors are non-fatal and deliberately leave the Circuit Relay connection available as fallback.
