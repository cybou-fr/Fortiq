use crate::canonical::codec::{to_canonical_cbor, CodecError};
use crate::canonical::records::ObjectTbs;
use crate::canonical::types::{KeyId, ObjectId};
use ed25519_dalek::{Signature, Signer as _, SigningKey, Verifier as _, VerifyingKey};
use sha3::{Digest, Sha3_256};
use thiserror::Error;

/// Domain separator for canonical object signature verification.
pub const OBJECT_SIG_DOMAIN: &[u8] = b"FORTIQ-SIG-v1:";

/// Domain separator for canonical ObjectId computation.
pub const OBJECT_ID_DOMAIN: &[u8] = b"FORTIQ-OBJECT-ID-v1:";
pub const SIGNING_KEY_ID_DOMAIN: &[u8] = b"FORTIQ-SIGNING-KEY-ID-v1:";

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

/// Ed25519 signing implementation used by the current classical crypto profile.
pub struct Ed25519Signer {
    signing_key: SigningKey,
    key_id: KeyId,
}

impl std::fmt::Debug for Ed25519Signer {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("Ed25519Signer")
            .field("key_id", &self.key_id)
            .finish_non_exhaustive()
    }
}

impl Ed25519Signer {
    pub fn from_seed(seed: [u8; 32]) -> Self {
        let signing_key = SigningKey::from_bytes(&seed);
        let key_id = derive_signing_key_id(signing_key.verifying_key().as_bytes());
        Self {
            signing_key,
            key_id,
        }
    }

    pub fn public_key(&self) -> [u8; 32] {
        self.signing_key.verifying_key().to_bytes()
    }
}

impl Signer for Ed25519Signer {
    fn sign(&self, domain_separated_data: &[u8]) -> Result<Vec<u8>, SigningError> {
        Ok(self
            .signing_key
            .sign(domain_separated_data)
            .to_bytes()
            .to_vec())
    }

    fn key_id(&self) -> KeyId {
        self.key_id
    }
}

pub struct Ed25519Verifier(VerifyingKey);

impl Ed25519Verifier {
    pub fn from_public_key(public_key: &[u8]) -> Result<Self, SigningError> {
        let bytes: [u8; 32] = public_key.try_into().map_err(|_| {
            SigningError::VerificationFailed("Ed25519 public key must be 32 bytes".into())
        })?;
        VerifyingKey::from_bytes(&bytes)
            .map(Self)
            .map_err(|error| SigningError::VerificationFailed(error.to_string()))
    }
}

impl Verifier for Ed25519Verifier {
    fn verify(&self, domain_separated_data: &[u8], signature: &[u8]) -> Result<(), SigningError> {
        let signature = Signature::from_slice(signature)
            .map_err(|error| SigningError::VerificationFailed(error.to_string()))?;
        self.0
            .verify(domain_separated_data, &signature)
            .map_err(|error| SigningError::VerificationFailed(error.to_string()))
    }
}

pub fn derive_signing_key_id(public_key: &[u8]) -> KeyId {
    let mut hasher = Sha3_256::new();
    hasher.update(SIGNING_KEY_ID_DOMAIN);
    hasher.update(public_key);
    KeyId::from_bytes(hasher.finalize().into())
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
