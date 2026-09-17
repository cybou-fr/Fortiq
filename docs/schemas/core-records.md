# Reference Core Records

Conceptual, not final Rust ABI.

```rust
struct SignedObject {
    tbs: ObjectTbs,
    signature: Vec<u8>,
}

struct ObjectTbs {
    version: u16,
    network_id: NetworkId,
    segment_id: Option<SegmentId>,
    storage_class: StorageClass,
    writer_key_id: KeyId,
    writer_stream_id: StreamId,
    writer_seq: u64,
    prev_pack_id: Option<ObjectId>,
    crypto_profile: CryptoProfileId,
    envelope_set_digest: [u8; 32],
    ciphertext_digest: [u8; 32],
    ciphertext_len: u64,
}

struct RecipientEnvelope {
    key_id: KeyId,
    key_epoch: u64,
    hpke_enc: Vec<u8>,
    sealed_key: Vec<u8>,
}

struct EventPackPlaintext {
    schema_version: u16,
    ticket_id: Option<TicketId>,
    ticket_crypto_epoch: Option<u64>,
    pack_nonce: [u8; 16],
    events: Vec<LogicalEvent>,
}

struct BlobManifest {
    version: u16,
    network_id: NetworkId,
    segment_id: SegmentId,
    blob_id: BlobId,
    ciphertext_len: u64,
    stripe_size: u32,
    rs_profile: RsProfile,
    stripes: Vec<StripeManifest>,
}
```
