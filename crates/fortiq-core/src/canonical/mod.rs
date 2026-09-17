//! Canonical Core implementation for FORTIQ Canonical Architecture v3.
//!
//! Provides deterministic CBOR wire records, strongly-typed identifiers,
//! strict decoder resource limits, and canonical domain-separated hashing.

pub mod codec;
pub mod records;
pub mod signing;
pub mod types;

#[cfg(test)]
mod test_vectors;

pub use codec::{from_canonical_cbor, to_canonical_cbor, CodecError, DecoderLimits};
pub use records::{
    BlobManifest, EventPackPlaintext, LogicalEvent, ObjectTbs, RecipientEnvelope, SignedObject,
    StripeManifest,
};
pub use signing::{
    compute_shard_checksum, compute_tbs_bytes, construct_signing_payload, derive_object_id, Signer,
    SigningError, Verifier, OBJECT_ID_DOMAIN, OBJECT_SIG_DOMAIN,
};
pub use types::{
    BlobId, CryptoProfileId, EntityId, KeyId, NetworkId, ObjectId, OwnerId, RsProfile, SegmentId,
    StorageClass, StreamId, TicketId,
};
