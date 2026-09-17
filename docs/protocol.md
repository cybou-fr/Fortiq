# FORTIQ — Protocol Specification (Canonical v3)

**Status:** Active wire protocol specification  
**Source of Truth:** `FORTIQ_CANONICAL_ARCH_v3` (supersedes v2 and prototype drafts)  
**Date:** 2026-09-17  

---

## 1. Transport and Wire Identity (Layer 0)

FORTIQ nodes communicate over **libp2p** using native QUIC (`quic-v1`). For peers behind restrictive firewalls or symmetric NAT, communication traverses **Circuit Relay v2**, and direct connectivity is established whenever possible via **DCUtR** (Direct Connection Upgrade through Relay). Peer discovery in the private `fortiq` namespace uses the libp2p **Rendezvous** protocol.

### Wire Identity vs Application Authority
- The remote `PeerId` is cryptographically authenticated by the libp2p secure transport.
- **Critical Invariant:** `PeerId` represents network transport identity only. Holding an established QUIC connection or authenticated `PeerId` conveys zero application-level authority or tenant data access. Application permissions require signed Genesis certificates, Segment descriptors, and valid `TicketAccessEpoch` tokens.

---

## 2. Serialization & Canonical Object Identification

Hash- and signature-critical wire records use **Deterministic CBOR** (RFC 8949).

### Determinism Rules
- Fixed protocol structures prefer **arrays with fixed field ordering** over maps to eliminate map-key sorting ambiguities and reduce wire overhead.
- Indefinite-length containers are strictly forbidden in signed structures.
- Floats are prohibited in canonical structures unless explicitly tagged and normalized.

### Decoder Resource Limits
To prevent parser denial-of-service (DoS) attacks, all decoders enforce strict limits:
- **Maximum input buffer:** Strictly capped per protocol family (see Resource Limits).
- **Maximum nesting depth:** Cap of 8 levels.
- **Maximum array length:** Bounded to explicit schema limits.
- **Maximum byte-string / text length:** Bounded to payload maximums.
- Any malformed, indefinite-length, or out-of-spec record fails closed and drops the stream.

### Canonical Object Identifier (`ObjectId`)
Objects never include their own identifier in the bytes used to compute the ID. Object identification follows the canonical domain-separation pattern:

$$\text{TBS} = \text{canonical\_cbor}(\text{ObjectHeader} \mathbin{\Vert} \text{CiphertextDescriptor} \mathbin{\Vert} \text{CiphertextDigest})$$
$$\text{sig} = \text{Sign}(\text{AuthorKey}, \text{"FORTIQ-SIG-v1"} \mathbin{\Vert} \text{TBS})$$
$$\text{ObjectId} = \text{SHA3-256}(\text{"FORTIQ-OBJECT-ID-v1"} \mathbin{\Vert} \text{TBS} \mathbin{\Vert} \text{sig})$$

Storage payloads and individual Reed–Solomon shards additionally employ `BLAKE3-256` for ultra-high-throughput integrity checksumming. Plaintext file hashes are never leaked in outer metadata.

---

## 3. Cryptographic Profiles

The wire format carries an explicit crypto profile. The current development runtime uses **`FortiqClassicalDev1`** with the classical primitives implemented by the Rust crates. **`FortiqPq1`** is reserved for the post-quantum target and is not a deployed security claim yet.

The reserved **FORTIQ-PQ1** target is defined as:

- **Key Encapsulation (KEM):** Hybrid `MLKEM768-X25519` via HPKE (RFC 9180 profile).
- **Digital Signatures (SIG):** `ML-DSA-65` (FIPS 204).
- **Symmetric Cipher (AEAD):** `ChaCha20Poly1305` (RFC 8439) with 256-bit keys and 96-bit nonces.
- **Key Derivation (KDF):** HKDF-SHA256 / SHAKE256 pinned profile.

### One Payload, $N$ Recipient Envelopes
FORTIQ mandates that ciphertext payloads are never duplicated per recipient:

```text
               +---------------------------------------+
               |        Plaintext EventPack / Blob      |
               +---------------------------------------+
                                   |
                         Generate random DEK
                                   |
                                   v
               +---------------------------------------+
               | AEAD Encrypt (ChaCha20Poly1305) once   |
               +---------------------------------------+
                                   |
                                   v
                          Single Ciphertext
                                   |
            +----------------------+----------------------+
            |                      |                      |
            v                      v                      v
     HPKE Seal DEK          HPKE Seal DEK          HPKE Seal DEK
   for Recipient A        for Recipient B        for Recipient N
```

Each `RecipientEnvelope` encapsulates:
- `key_id`: Segment-scoped identifier derived from recipient public key and epoch;
- `key_epoch`: Active key generation counter;
- `hpke_enc`: Encapsulated KEM shared secret;
- `sealed_key`: Symmetric DEK encrypted with the HPKE context.

The HPKE context contextually binds `NetworkId`, `SegmentId`, domain purpose, `key_id`, `key_epoch`, and `crypto_profile`. The outer AEAD additional authenticated data (AAD) binds the `NetworkId`, `SegmentId`, schema version, pack ID, author key ID, and digest of the envelope set.

---

## 4. Protocol Families & Wire Substreams

FORTIQ categorizes its interactions into discrete, versioned protocol handlers:

```text
/fortiq/genesis/1     -- Root Genesis distribution & verification
/fortiq/control/1     -- Segment descriptors, enrollment, revocations
/fortiq/head/1        -- Ephemeral signed head advertisements
/fortiq/inventory/1   -- Object inventories and DAG anti-entropy
/fortiq/object/1      -- Bounded RPC for small control objects and EventPacks
/fortiq/shard/1       -- High-throughput streaming protocol for RS shards & blobs
/fortiq/ticket/next   -- Ticket lifecycle, AccessEpochs, and reducer sync
/fortiq/shell/next    -- Ticket-gated PTY/ConPTY streaming with immediate revoke
```

### 4.1. Genesis & Control (`/fortiq/genesis/1`, `/fortiq/control/1`)
- Bounded request/response RPCs delivering signed Genesis, Segment Descriptors, and Owner Revocation Lists.
- Verifies network membership and validates the root `ML-DSA-65` signature against the pinned Genesis root.

### 4.2. Synchronization & Heads (`/fortiq/head/1`, `/fortiq/inventory/1`)
- **Head Advertisements:** Writers periodically broadcast signed, ephemeral `HeadAdvertisement` records announcing their current stream sequence and latest `ObjectId`.
- **Anti-Entropy Inventories:** Peers compare known frontiers. A node requesting synchronisation sends an inventory range; the responder transmits the missing `ObjectId` sequence, which the requester retrieves via `/fortiq/object/1` or `/fortiq/shard/1`.

### 4.3. Object Exchange (`/fortiq/object/1`)
- Optimized request/response protocol for payloads $\le 256\text{ KiB}$ (Control objects, small EventPacks, Snapshot headers).
- Responds with the canonical signed CBOR record.

### 4.4. Shard Streaming Protocol (`/fortiq/shard/1`)
Large payloads (files, video diagnostics, large state snapshots) use streaming substreams rather than in-memory buffering:

```text
Sender                                                      Receiver
  |                                                            |
  |-- SHARD_OPEN { object_id, stripe_idx, shard_idx, len, hash }->|
  |                                                            |
  |<-- SHARD_ACCEPT / SHARD_REJECT ----------------------------|
  |                                                            |
  |-- SHARD_DATA (chunk 1 .. N) ------------------------------>|
  |                                                            |
  |-- SHARD_END { blake3_checksum } -------------------------->|
  |                                                            |
  |<-- ACK / NACK ---------------------------------------------|
```

- Every shard is verified against its declared `BLAKE3` hash **before** it is committed to storage or passed to the Reed–Solomon decoder.
- Transfer incorporates explicit backpressure, chunk flow control, and liveness timeouts (15s idle, 60s total per shard).

### 4.5. Ticket Administration (`/fortiq/ticket/next`)
- Coordinates ticket state events (`TicketCreated`, `TicketStateChanged`, `TicketCryptoEpochRotated`).
- Tickets are created solely by managed clients.
- Clients issue an `AccessEpoch` token when granting remote intervention permissions.

### 4.6. Shell Protocol (`/fortiq/shell/next`)
- **Admission Gate:** Requires `TicketState` $\in \{\text{OPEN}, \text{IN\_PROGRESS}\}$, active local `TicketAccessEpoch`, and a valid unexpired `OperatorSessionCertificate`.
- **Pseudoterminal Allocation:** Uses `portable-pty` (ConPTY on Windows, openpty on Unix).
- **Binary Framing:** Framed using `ShellFrame` packets with a 3-byte header (`tag: u8`, `len: u16` big-endian):
  - `0x00` (`Data`): Terminal byte stream (up to 64 KiB payload).
  - `0x01` (`Resize`): Terminal window geometry updates (`cols: u16`, `rows: u16`).
  - `0x02` (`Ping`): Keepalive telemetry sent every 15s.
  - `0x03` (`Pong`): Keepalive response.
- **Liveness Guard:** A 60-second inactivity timeout drops abandoned shell sessions and kills child processes.
- **Synchronous Client Revocation:** When a client revokes access or closes the ticket:
  1. Local `AccessEpoch` is immediately deleted from memory.
  2. Local shell process tree is terminated with extreme prejudice (`SIGKILL` / `TerminateProcess`).
  3. Revocation event is fsynced to disk and dispatched to the network.
- **Session Certificate Lifecycle:** Operator lock or certificate expiry cancels active terminal forwarding and prevents an in-flight shell handshake from completing. The managed node re-reads the ticket immediately before authorization, so close, revoke, and epoch rotation during the handshake fail closed.
- **Legacy Rejection:** `/fortiq/shell/2.0` is disabled. Clients must use `/fortiq/shell/next` through the service IPC path.

---

## 5. Resource Limits and DoS Bounds

To ensure resilience against resource exhaustion, all protocols enforce strict upper bounds:

| Message Type / Object | Maximum Size | Timeout | Notes |
| :--- | :--- | :--- | :--- |
| **Genesis Record** | $64\text{ KiB}$ | $10\text{ s}$ | Pinned root record |
| **Segment Descriptor** | $32\text{ KiB}$ | $10\text{ s}$ | Signed by Owner Root |
| **Head Advertisement** | $16\text{ KiB}$ | $5\text{ s}$ | Signed ephemeral hint |
| **EventPack** | $256\text{ KiB}$ | $15\text{ s}$ | Hard pack ceiling |
| **Blob Manifest** | $1\text{ MiB}$ | $30\text{ s}$ | Contains stripe metadata |
| **Storage Shard** | $4\text{ MiB}$ | $60\text{ s}$ | Streamed via `/fortiq/shard/1` |
| **Shell Frame** | $64\text{ KiB}$ | $60\text{ s}$ (idle) | Binary ConPTY/PTY framing |

Protocol implementations must fail closed on unrecognized versions, downgrade attempts, or payload sizes exceeding these limits.

