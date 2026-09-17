use crate::canonical::codec::{to_canonical_cbor, CodecError};
use crate::canonical::records::ObjectTbs;
use crate::canonical::types::{KeyId, ObjectId};
use sha3::{Digest, Sha3_256};
use thiserror::Error;

/// Domain separator for canonical object signature verification.
pub const OBJECT_SIG_DOMAIN: &[u8] = b"FORTIQ-SIG-v1:";

/// Domain separator for canonical ObjectId computation.
pub const OBJECT_ID_DOMAIN: &[u8] = b"FORTIQ-OBJECT-ID-v1:";

#[derive(Error, Debug)]
pub enum SigningError {
    #[error("serialization error: {0}")]
    Serialization(#[from] CodecError),
    #[error("cryptographic signing failed: {0}")]
    SigningFailed(String),
    #[error("signature verification failed: {0}")]
    VerificationFailed(String),
}

/// Abstract signing trait for signing canonical objects.
pub trait Signer {
    /// Sign domain-separated bytes using the author's private key.
    fn sign(&self, domain_separated_data: &[u8]) -> Result<Vec<u8>, SigningError>;
    /// Return the public KeyId of this signer.
    fn key_id(&self) -> KeyId;
}

/// Abstract signature verification trait.
pub trait Verifier {
    /// Verify a signature over domain-separated bytes.
    fn verify(&self, domain_separated_data: &[u8], signature: &[u8]) -> Result<(), SigningError>;
}

/// Derive the canonical `ObjectId` from TBS bytes and its valid signature.
///
/// Invariant:
/// ObjectId = SHA3-256("FORTIQ-OBJECT-ID-v1:" || TBS || sig)
pub fn derive_object_id(tbs_bytes: &[u8], signature: &[u8]) -> ObjectId {
    let mut hasher = Sha3_256::new();
    hasher.update(OBJECT_ID_DOMAIN);
    hasher.update(tbs_bytes);
    hasher.update(signature);
    let digest: [u8; 32] = hasher.finalize().into();
    ObjectId::from_bytes(digest)
}

/// Compute the deterministic CBOR serialized bytes of an `ObjectTbs`.
pub fn compute_tbs_bytes(tbs: &ObjectTbs) -> Result<Vec<u8>, CodecError> {
    to_canonical_cbor(tbs)
}

/// Helper function to construct the domain-separated signing payload.
pub fn construct_signing_payload(tbs_bytes: &[u8]) -> Vec<u8> {
    let mut payload = Vec::with_capacity(OBJECT_SIG_DOMAIN.len() + tbs_bytes.len());
    payload.extend_from_slice(OBJECT_SIG_DOMAIN);
    payload.extend_from_slice(tbs_bytes);
    payload
}

/// Compute the fast BLAKE3-256 storage shard checksum.
pub fn compute_shard_checksum(shard_bytes: &[u8]) -> [u8; 32] {
    let hash = blake3::hash(shard_bytes);
    *hash.as_bytes()
}
