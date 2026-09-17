//! Sync Engine for FORTIQ Canonical Architecture v3.
//!
//! Provides Head Advertisements, Tail Sync with fork detection,
//! Anti-Entropy inventory exchange, and Encrypted Snapshots.

pub mod head;
pub mod inventory;
pub mod snapshot;
pub mod tail;

#[cfg(any())]
mod tests;

pub use head::{
    HeadAdvertisement, HeadError, HeadTracker, SegmentHeadIndex, HEAD_ADV_SIG_DOMAIN,
    MAX_HEAD_ADV_BYTES,
};
pub use inventory::{
    AntiEntropyEngine, InventoryRequest, InventoryResponse, ReconciliationPlan, INVENTORY_PROTOCOL,
    MAX_INVENTORY_PAGE_ITEMS,
};
pub use snapshot::{EncryptedSnapshot, SnapshotSyncError, MAX_SNAPSHOT_BYTES, SNAPSHOT_SIG_DOMAIN};
pub use tail::{PackHeaderInfo, TailSyncError, TailSyncPlanner, TailSyncStatus};
