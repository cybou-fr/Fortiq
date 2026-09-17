//! Physical Garbage Collection and Storage Purge Execution.
//!
//! Hard Invariants (docs/spec/14-deletion-gc-anti-resurrection.md):
//! - Physical purge irreversibly reclaims ciphertext shards from disk/storage.
//! - Requires both a valid SignedTombstone and a valid PurgeAuthorization.
//! - Prohibited before `earliest_gc_time`.
//! - Atomically updates AntiResurrectionTracker to permanently blacklist purged assets.

use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::canonical::deletion::anti_resurrection::AntiResurrectionTracker;
use crate::canonical::deletion::purge_auth::{PurgeAuthError, PurgeAuthorization};
use crate::canonical::deletion::tombstone::{SignedTombstone, TombstoneError};
use crate::canonical::signing::Verifier;
use crate::canonical::types::ObjectId;

#[derive(Debug, Error)]
pub enum GcError {
    #[error("Tombstone verification failed: {0}")]
    Tombstone(#[from] TombstoneError),
    #[error("Purge authorization verification failed: {0}")]
    PurgeAuth(#[from] PurgeAuthError),
    #[error(
        "Tombstone mismatch: PurgeAuth expects {expected:?}, provided Tombstone is {actual:?}"
    )]
    TombstoneMismatch {
        expected: ObjectId,
        actual: ObjectId,
    },
    #[error("Storage I/O error: {0}")]
    Storage(String),
}

/// Abstract storage backend capable of deleting shards and event packs.
pub trait PurgeableStorage {
    /// Deletes a physical ciphertext shard by its BLAKE3 checksum.
    /// Returns the number of reclaimed bytes, or Ok(0) if not found.
    fn delete_shard(&mut self, checksum: &[u8; 32]) -> Result<u64, String>;

    /// Deletes an event pack or logical object by its ObjectId.
    /// Returns the number of reclaimed bytes, or Ok(0) if not found.
    fn delete_object(&mut self, object_id: &ObjectId) -> Result<u64, String>;
}

/// Audit receipt proving successful physical garbage collection execution.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PurgeAuditReceipt {
    pub tombstone_id: ObjectId,
    pub purged_objects: Vec<ObjectId>,
    pub purged_shards: Vec<[u8; 32]>,
    pub reclaimed_bytes: u64,
    pub executed_at: u64,
}

/// Engine executing verified physical garbage collection.
pub struct PhysicalGarbageCollector;

impl PhysicalGarbageCollector {
    /// Evaluates and executes physical garbage collection on the given storage backend.
    pub fn execute_purge(
        storage: &mut impl PurgeableStorage,
        tracker: &mut AntiResurrectionTracker,
        tombstone: &SignedTombstone,
        purge_auth: &PurgeAuthorization,
        verifier: &impl Verifier,
        current_time: u64,
    ) -> Result<PurgeAuditReceipt, GcError> {
        // 1. Verify Tombstone signature
        tombstone.verify(verifier)?;
        let computed_tombstone_id = tombstone.tombstone_id();

        // 2. Verify PurgeAuthorization signature
        purge_auth.verify(verifier)?;

        // 3. Verify Tombstone link matches
        if purge_auth.tbs.tombstone_id != computed_tombstone_id {
            return Err(GcError::TombstoneMismatch {
                expected: purge_auth.tbs.tombstone_id,
                actual: computed_tombstone_id,
            });
        }

        // 4. Check delayed earliest GC time threshold
        if !purge_auth.is_ready_for_gc(current_time) {
            return Err(GcError::PurgeAuth(PurgeAuthError::PrematureGc {
                current: current_time,
                earliest: purge_auth.tbs.earliest_gc_time,
            }));
        }

        // 5. Physically delete target shards
        let mut total_reclaimed_bytes = 0u64;
        for shard_checksum in &purge_auth.tbs.target_shards {
            let bytes = storage
                .delete_shard(shard_checksum)
                .map_err(GcError::Storage)?;
            total_reclaimed_bytes += bytes;
        }

        // 6. Physically delete target objects
        for object_id in &purge_auth.tbs.target_objects {
            let bytes = storage.delete_object(object_id).map_err(GcError::Storage)?;
            total_reclaimed_bytes += bytes;
        }

        // 7. Update anti-resurrection tracker so purged state can NEVER be resurrected
        tracker.record_purge(
            &purge_auth.tbs.target_objects,
            &purge_auth.tbs.target_shards,
        );

        Ok(PurgeAuditReceipt {
            tombstone_id: computed_tombstone_id,
            purged_objects: purge_auth.tbs.target_objects.clone(),
            purged_shards: purge_auth.tbs.target_shards.clone(),
            reclaimed_bytes: total_reclaimed_bytes,
            executed_at: current_time,
        })
    }
}
