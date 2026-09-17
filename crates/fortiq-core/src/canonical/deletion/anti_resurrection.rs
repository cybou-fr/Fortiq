//! Anti-Resurrection Tracker and Deletion Checkpoints.
//!
//! Hard Invariants (docs/spec/14-deletion-gc-anti-resurrection.md):
//! - "Do not delete the only evidence that something was deleted."
//! - Retain Tombstone and Purge records to prevent stale or malicious nodes from
//!   re-introducing deleted state during tail sync, anti-entropy, or shard transfers.
//! - Client safety: Purging or tombstoning events cannot recreate a dead client AccessEpoch.

use std::collections::{HashMap, HashSet};
use thiserror::Error;

use crate::canonical::types::ObjectId;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum AntiResurrectionError {
    #[error("Object {0:?} was logically deleted (tombstoned) and cannot be resurrected")]
    ObjectTombstoned(ObjectId),
    #[error("Object {0:?} was physically purged and cannot be re-admitted")]
    ObjectPurged(ObjectId),
    #[error("Shard {0:?} was physically purged and cannot be re-admitted")]
    ShardPurged([u8; 32]),
}

/// Sovereign Anti-Resurrection Tracker enforcing permanent rejection of resurrected state.
#[derive(Debug, Default, Clone)]
pub struct AntiResurrectionTracker {
    /// Maps target object IDs to the tombstone ID that logically deleted them.
    tombstoned_objects: HashMap<ObjectId, ObjectId>,
    /// Set of object IDs that have been authorized and physically purged.
    purged_objects: HashSet<ObjectId>,
    /// Set of shard checksums (BLAKE3) that have been authorized and physically purged.
    purged_shards: HashSet<[u8; 32]>,
}

impl AntiResurrectionTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Records a new Tombstone for an object.
    pub fn record_tombstone(&mut self, tombstone_id: ObjectId, target_object_id: ObjectId) {
        self.tombstoned_objects
            .insert(target_object_id, tombstone_id);
    }

    /// Records a physical purge of objects and shards.
    pub fn record_purge(&mut self, objects: &[ObjectId], shards: &[[u8; 32]]) {
        for obj in objects {
            self.purged_objects.insert(*obj);
        }
        for shard in shards {
            self.purged_shards.insert(*shard);
        }
    }

    /// Returns true if an object has been logically tombstoned.
    pub fn is_tombstoned(&self, object_id: &ObjectId) -> bool {
        self.tombstoned_objects.contains_key(object_id)
    }

    /// Returns true if an object has been physically purged.
    pub fn is_object_purged(&self, object_id: &ObjectId) -> bool {
        self.purged_objects.contains(object_id)
    }

    /// Returns true if a shard has been physically purged.
    pub fn is_shard_purged(&self, shard_checksum: &[u8; 32]) -> bool {
        self.purged_shards.contains(shard_checksum)
    }

    /// Validates whether an incoming object is allowed to be admitted into local storage/graph.
    pub fn check_object_admission(
        &self,
        object_id: &ObjectId,
    ) -> Result<(), AntiResurrectionError> {
        if self.is_object_purged(object_id) {
            return Err(AntiResurrectionError::ObjectPurged(*object_id));
        }
        if self.is_tombstoned(object_id) {
            return Err(AntiResurrectionError::ObjectTombstoned(*object_id));
        }
        Ok(())
    }

    /// Validates whether an incoming shard is allowed to be accepted into local storage.
    pub fn check_shard_admission(
        &self,
        shard_checksum: &[u8; 32],
    ) -> Result<(), AntiResurrectionError> {
        if self.is_shard_purged(shard_checksum) {
            return Err(AntiResurrectionError::ShardPurged(*shard_checksum));
        }
        Ok(())
    }

    /// Returns total count of tracked deletions.
    pub fn stats(&self) -> (usize, usize, usize) {
        (
            self.tombstoned_objects.len(),
            self.purged_objects.len(),
            self.purged_shards.len(),
        )
    }
}
