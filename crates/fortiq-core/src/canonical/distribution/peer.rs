//! Eligible Storage Peer capability advertisements and failure-domain attributes.
//!
//! Hard Invariants & Specifications (docs/spec/11-placement-repair-availability.md):
//! - The network NEVER assumes every peer is a storage peer.
//! - Peers advertise explicit capacity, max shard size, and free storage budget.
//! - Invariant 3: PeerId is transport-only, never a domain identity.
//! - Physical machine ID and site ID enforce strict failure-domain isolation.

use crate::canonical::types::EntityId;
use serde::{Deserialize, Serialize};

/// Storage peer capability descriptor.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StoragePeerCapability {
    /// Canonical sovereign entity identity.
    pub node_id: EntityId,
    /// Transport-only PeerId (libp2p Multiaddr/PeerId).
    pub peer_id: String,
    /// Unique 128-bit physical machine/chassis identifier for hardware failure isolation.
    pub machine_id: [u8; 16],
    /// Optional site or datacenter rack/zone identifier.
    pub site_id: Option<String>,
    /// Total committed storage capacity in bytes.
    pub capacity_bytes: u64,
    /// Maximum individual shard size accepted in bytes.
    pub max_shard_bytes: u64,
    /// Current free available storage budget in bytes.
    pub free_budget_bytes: u64,
    /// Storage weighting factor for rendezvous candidate selection (default 100).
    pub weight: u32,
}

impl StoragePeerCapability {
    pub fn new(
        node_id: EntityId,
        peer_id: impl Into<String>,
        machine_id: [u8; 16],
        site_id: Option<String>,
        capacity_bytes: u64,
        free_budget_bytes: u64,
    ) -> Self {
        Self {
            node_id,
            peer_id: peer_id.into(),
            machine_id,
            site_id,
            capacity_bytes,
            max_shard_bytes: 4 * 1024 * 1024, // default 4 MiB max shard
            free_budget_bytes,
            weight: 100,
        }
    }

    /// Checks if this peer can accommodate a shard of given size.
    pub fn can_store(&self, shard_size_bytes: u64) -> bool {
        shard_size_bytes <= self.max_shard_bytes && shard_size_bytes <= self.free_budget_bytes
    }
}
