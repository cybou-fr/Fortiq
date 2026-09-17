//! Anti-Entropy Inventory Protocol (`/fortiq/inventory/1`).
//!
//! Hard Invariants & Specifications (docs/spec/12-sync-heads-anti-entropy.md, docs/spec/17-protocol-map-resource-limits.md):
//! - Periodic peer-to-peer inventory exchange reconciles:
//!   - missing manifests;
//!   - missing packs;
//!   - shard availability;
//!   - tombstones/control epochs.
//! - Bounded inventory requests and responses (<= 128 KiB).
//! - Deterministic set-difference reconciliation plan.

use crate::canonical::types::{ObjectId, SegmentId};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

/// Protocol identifier for inventory anti-entropy exchange.
pub const INVENTORY_PROTOCOL: &str = "/fortiq/inventory/1";

/// Maximum allowed items per single inventory message page to strictly respect resource limits.
pub const MAX_INVENTORY_PAGE_ITEMS: usize = 1024;

/// Inventory request sent from a querying node to a peer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct InventoryRequest {
    pub segment_id: SegmentId,
    pub known_pack_ids: Vec<ObjectId>,
    pub known_manifest_ids: Vec<ObjectId>,
    pub known_tombstones: Vec<ObjectId>,
    pub access_epoch: u64,
}

impl InventoryRequest {
    pub fn new(
        segment_id: SegmentId,
        known_pack_ids: Vec<ObjectId>,
        known_manifest_ids: Vec<ObjectId>,
        known_tombstones: Vec<ObjectId>,
        access_epoch: u64,
    ) -> Self {
        Self {
            segment_id,
            known_pack_ids,
            known_manifest_ids,
            known_tombstones,
            access_epoch,
        }
    }
}

/// Inventory response returned by a peer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct InventoryResponse {
    pub segment_id: SegmentId,
    pub available_pack_ids: Vec<ObjectId>,
    pub available_manifest_ids: Vec<ObjectId>,
    pub tombstones: Vec<ObjectId>,
    pub access_epoch: u64,
}

impl InventoryResponse {
    pub fn new(
        segment_id: SegmentId,
        available_pack_ids: Vec<ObjectId>,
        available_manifest_ids: Vec<ObjectId>,
        tombstones: Vec<ObjectId>,
        access_epoch: u64,
    ) -> Self {
        Self {
            segment_id,
            available_pack_ids,
            available_manifest_ids,
            tombstones,
            access_epoch,
        }
    }
}

/// Computed anti-entropy reconciliation plan between local state and a remote peer.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ReconciliationPlan {
    /// Packs the remote peer holds that the local node is missing (to be pulled).
    pub packs_to_pull: Vec<ObjectId>,
    /// Packs the local node holds that the remote peer is missing (to be pushed/offered).
    pub packs_to_push: Vec<ObjectId>,
    /// Manifests the remote peer holds that the local node is missing (to be pulled).
    pub manifests_to_pull: Vec<ObjectId>,
    /// Manifests the local node holds that the remote peer is missing (to be pushed/offered).
    pub manifests_to_push: Vec<ObjectId>,
    /// Tombstones the remote peer holds that the local node is missing (to be applied).
    pub tombstones_to_pull: Vec<ObjectId>,
    /// Tombstones the local node holds that the remote peer is missing (to be pushed).
    pub tombstones_to_push: Vec<ObjectId>,
}

impl ReconciliationPlan {
    /// Returns true if both nodes are completely synchronized.
    pub fn is_empty(&self) -> bool {
        self.packs_to_pull.is_empty()
            && self.packs_to_push.is_empty()
            && self.manifests_to_pull.is_empty()
            && self.manifests_to_push.is_empty()
            && self.tombstones_to_pull.is_empty()
            && self.tombstones_to_push.is_empty()
    }
}

/// Anti-entropy reconciliation engine.
pub struct AntiEntropyEngine;

impl AntiEntropyEngine {
    /// Computes symmetric set difference between local inventory and remote response.
    pub fn reconcile(
        local_known_packs: &[ObjectId],
        local_known_manifests: &[ObjectId],
        local_known_tombstones: &[ObjectId],
        remote_response: &InventoryResponse,
    ) -> ReconciliationPlan {
        let local_pack_set: HashSet<ObjectId> = local_known_packs.iter().copied().collect();
        let remote_pack_set: HashSet<ObjectId> =
            remote_response.available_pack_ids.iter().copied().collect();

        let local_manifest_set: HashSet<ObjectId> = local_known_manifests.iter().copied().collect();
        let remote_manifest_set: HashSet<ObjectId> = remote_response
            .available_manifest_ids
            .iter()
            .copied()
            .collect();

        let local_tombstone_set: HashSet<ObjectId> =
            local_known_tombstones.iter().copied().collect();
        let remote_tombstone_set: HashSet<ObjectId> =
            remote_response.tombstones.iter().copied().collect();

        // Packs to pull: remote has, local does not
        let packs_to_pull: Vec<ObjectId> = remote_response
            .available_pack_ids
            .iter()
            .filter(|id| !local_pack_set.contains(id))
            .copied()
            .collect();

        // Packs to push: local has, remote does not
        let packs_to_push: Vec<ObjectId> = local_known_packs
            .iter()
            .filter(|id| !remote_pack_set.contains(id))
            .copied()
            .collect();

        // Manifests to pull
        let manifests_to_pull: Vec<ObjectId> = remote_response
            .available_manifest_ids
            .iter()
            .filter(|id| !local_manifest_set.contains(id))
            .copied()
            .collect();

        // Manifests to push
        let manifests_to_push: Vec<ObjectId> = local_known_manifests
            .iter()
            .filter(|id| !remote_manifest_set.contains(id))
            .copied()
            .collect();

        // Tombstones to pull
        let tombstones_to_pull: Vec<ObjectId> = remote_response
            .tombstones
            .iter()
            .filter(|id| !local_tombstone_set.contains(id))
            .copied()
            .collect();

        // Tombstones to push
        let tombstones_to_push: Vec<ObjectId> = local_known_tombstones
            .iter()
            .filter(|id| !remote_tombstone_set.contains(id))
            .copied()
            .collect();

        ReconciliationPlan {
            packs_to_pull,
            packs_to_push,
            manifests_to_pull,
            manifests_to_push,
            tombstones_to_pull,
            tombstones_to_push,
        }
    }
}
