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
