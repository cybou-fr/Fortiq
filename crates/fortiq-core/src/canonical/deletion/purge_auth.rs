//! Cryptographically signed physical purge authorization.
//!
//! Hard Invariants (docs/spec/14-deletion-gc-anti-resurrection.md):
//! - PurgeAuthorization is a separate irreversible physical storage action.
//! - References:
//!   - TombstoneId;
//!   - target object/blob/shard IDs;
//!   - earliest GC time;
//!   - admin signature.
//! - Separation principle: UI deletes are logical (Tombstone) before physical purge.
//! - Physical purge is explicit and delayed until `current_time >= earliest_gc_time`.

use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::canonical::codec::serde_bytes;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId, ObjectId};

pub const PURGE_AUTH_SIG_DOMAIN: &[u8] = b"FORTIQ-PURGE-AUTH-v3\x00";

#[derive(Debug, Error)]
pub enum PurgeAuthError {
    #[error("Signing error: {0}")]
    Signing(#[from] SigningError),
    #[error("Verification failed: {0}")]
    VerificationFailed(String),
    #[error("Premature GC attempt: current time {current} < earliest GC time {earliest}")]
    PrematureGc { current: u64, earliest: u64 },
    #[error("Tombstone mismatch: expected {expected:?}, got {actual:?}")]
    TombstoneMismatch {
        expected: ObjectId,
        actual: ObjectId,
    },
}

/// To-Be-Signed (TBS) payload for a physical purge authorization.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PurgeAuthorizationTbs {
    pub tombstone_id: ObjectId,
    pub target_objects: Vec<ObjectId>,
    pub target_shards: Vec<[u8; 32]>,
    pub earliest_gc_time: u64,
    pub authorized_by: EntityId,
}

impl PurgeAuthorizationTbs {
    /// Serializes TBS payload with domain separation for signing.
    pub fn compute_signing_payload(&self) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(PURGE_AUTH_SIG_DOMAIN);
        payload.extend_from_slice(self.tombstone_id.as_bytes());

        payload.extend_from_slice(&(self.target_objects.len() as u32).to_be_bytes());
        for obj in &self.target_objects {
            payload.extend_from_slice(obj.as_bytes());
        }

        payload.extend_from_slice(&(self.target_shards.len() as u32).to_be_bytes());
        for shard in &self.target_shards {
            payload.extend_from_slice(shard);
        }

        payload.extend_from_slice(&self.earliest_gc_time.to_be_bytes());
        payload.extend_from_slice(self.authorized_by.as_bytes());
        payload
    }
}

/// Signed Purge Authorization permitting physical garbage collection of ciphertext.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PurgeAuthorization {
    pub tbs: PurgeAuthorizationTbs,
    pub author_key_id: KeyId,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

impl PurgeAuthorization {
    /// Creates and signs a new physical purge authorization.
    pub fn create(
        tombstone_id: ObjectId,
        target_objects: Vec<ObjectId>,
        target_shards: Vec<[u8; 32]>,
        earliest_gc_time: u64,
        authorized_by: EntityId,
        signer: &impl Signer,
    ) -> Result<Self, PurgeAuthError> {
        let tbs = PurgeAuthorizationTbs {
            tombstone_id,
            target_objects,
            target_shards,
            earliest_gc_time,
            authorized_by,
        };
        let payload = tbs.compute_signing_payload();
        let signature = signer.sign(&payload)?;

        Ok(Self {
            tbs,
            author_key_id: signer.key_id(),
            signature,
        })
    }

    /// Checks if physical garbage collection is permitted at `current_time`.
    pub fn is_ready_for_gc(&self, current_time: u64) -> bool {
        current_time >= self.tbs.earliest_gc_time
    }

    /// Verifies the authorization signature.
    pub fn verify(&self, verifier: &impl Verifier) -> Result<(), PurgeAuthError> {
        let payload = self.tbs.compute_signing_payload();
        verifier
            .verify(&payload, &self.signature)
            .map_err(|e| PurgeAuthError::VerificationFailed(e.to_string()))
    }
}
