//! Cryptographically signed logical deletion (Tombstone).
//!
//! Hard Invariants (docs/spec/14-deletion-gc-anti-resurrection.md):
//! - Admin-signed Tombstone is logical deletion: reducers stop presenting the target
//!   as active or canonical.
//! - Preserves deletion history so evidence of deletion is retained.
//! - Anti-resurrection: stale peers re-announcing deleted objects are rejected.

use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::canonical::codec::serde_bytes;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId, ObjectId};

pub const TOMBSTONE_SIG_DOMAIN: &[u8] = b"FORTIQ-TOMBSTONE-v3\x00";
pub const TOMBSTONE_ID_DOMAIN: &[u8] = b"FORTIQ-TOMBSTONE-ID-v3\x00";

#[derive(Debug, Error)]
pub enum TombstoneError {
    #[error("Signing error: {0}")]
    Signing(#[from] SigningError),
    #[error("Verification failed: {0}")]
    VerificationFailed(String),
}

/// To-Be-Signed (TBS) payload for a logical deletion Tombstone.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TombstoneTbs {
    pub target_object_id: ObjectId,
    pub deleted_by: EntityId,
    pub reason: String,
    pub deleted_at: u64,
}

impl TombstoneTbs {
    /// Serializes TBS payload with domain separation for signing.
    pub fn compute_signing_payload(&self) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(TOMBSTONE_SIG_DOMAIN);
        payload.extend_from_slice(self.target_object_id.as_bytes());
        payload.extend_from_slice(self.deleted_by.as_bytes());
        payload.extend_from_slice(self.reason.as_bytes());
        payload.extend_from_slice(&self.deleted_at.to_be_bytes());
        payload
    }
}

/// Signed Tombstone asserting logical deletion of an object in the event graph.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SignedTombstone {
    pub tbs: TombstoneTbs,
    pub author_key_id: KeyId,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

impl SignedTombstone {
    /// Creates and signs a new logical deletion Tombstone.
    pub fn create(
        target_object_id: ObjectId,
        deleted_by: EntityId,
        reason: impl Into<String>,
        deleted_at: u64,
        signer: &impl Signer,
    ) -> Result<Self, TombstoneError> {
        let tbs = TombstoneTbs {
            target_object_id,
            deleted_by,
            reason: reason.into(),
            deleted_at,
        };
        let payload = tbs.compute_signing_payload();
        let signature = signer.sign(&payload)?;

        Ok(Self {
            tbs,
            author_key_id: signer.key_id(),
            signature,
        })
    }

    /// Derives the canonical unique identifier for this Tombstone.
    pub fn tombstone_id(&self) -> ObjectId {
        let mut hasher = blake3::Hasher::new();
        hasher.update(TOMBSTONE_ID_DOMAIN);
        hasher.update(&self.tbs.compute_signing_payload());
        hasher.update(&self.signature);
        ObjectId::from_bytes(*hasher.finalize().as_bytes())
    }

    /// Verifies the cryptographic signature against an authorized verifier.
    pub fn verify(&self, verifier: &impl Verifier) -> Result<(), TombstoneError> {
        let payload = self.tbs.compute_signing_payload();
        verifier
            .verify(&payload, &self.signature)
            .map_err(|e| TombstoneError::VerificationFailed(e.to_string()))
    }
}
