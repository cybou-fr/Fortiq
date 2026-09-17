# FORTIQ development rules (Canonical Architecture v3)

- **Source of Truth**: All architectural and protocol changes must strictly conform to Canonical Architecture v3 documented in `docs/` (`docs/README.md`).

- **Identity & Transport**: Node `PeerId` is transport identity only. Transport connections never confer application authority.
- **Authority Root**: Network authority is rooted in signed Genesis. The Operator is not a machine; operator authority is portable through a 24-word mnemonic and expressed via ephemeral session certificates.
- **Cryptographic Segments**: Every client has an independent cryptographic Segment. Operator HPKE keys are derived deterministically per Segment. Compromise of Segment A must never decrypt Segment B.
- **Immutable Object Graph**: Application state is an immutable signed append-only event graph. Nobody edits an object in place. Updates create successor events.
- **Ticket & Shell Safety**: The client owns `TicketAccessEpoch`. A client close/revoke event must atomically invalidate local access and terminate active shells immediately, before distributed network convergence. Operator overrides cannot revive a closed epoch.
- **Deterministic Serialization**: Signed protocol structures use strict deterministic CBOR arrays (RFC 8949). Indefinite lengths, map sorting ambiguities, and duplicate keys are forbidden.
- **Tiered Storage**: Tiny control objects are highly replicated; medium state packs use bounded replication; large blobs and state use streaming Reed–Solomon erasure coding. Encrypt before RS; verify shard hashes before reconstruction.
- **Security & Zeroization**: Never print, log, transmit, or commit seed phrases, private keys, or plaintext DEKs. Sensitive memory must implement zeroization.
- **Bounded Inputs & Code Quality**: Keep all network inputs and parser allocations bounded. Avoid unsafe Rust.
- **Verification**: Before claiming completion, verify workspace build, unit/integration tests, formatting (`cargo fmt --check`), and Clippy (`cargo clippy --workspace --all-targets -- -D warnings`).


