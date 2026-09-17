//! Storage Class replication policies, adaptive Reed-Solomon profile selection,
//! and shard health assessment.
//!
//! Specifications (docs/spec/10-storage-classes-reed-solomon.md, docs/spec/11-placement-repair-availability.md):
//! - CONTROL: replicated in full to all eligible peers; no RS.
//! - STATE: replicated (R=3) if <64 KiB; RS if >=64 KiB.
//! - BLOB: always streaming Reed-Solomon in stripes.
//! - Adaptive profiles (~1.5x storage overhead):
//!   1 peer: (1,0), 2 peers: (1,1), 3-5 peers: (2,1), 6-8 peers: (4,2), 9+ peers: (6,3).

use crate::canonical::types::{RsProfile, StorageClass};
use serde::{Deserialize, Serialize};

/// Threshold in bytes under which StatePack objects use simple replication instead of RS.
pub const STATE_PACK_RS_THRESHOLD_BYTES: usize = 64 * 1024;

/// Target stripe size for streaming large blobs (4 MiB).
pub const DEFAULT_LOGICAL_STRIPE_SIZE_BYTES: usize = 4 * 1024 * 1024;

/// Determines whether an object of given storage class and length should be erasure-coded.
pub fn should_erasure_code(storage_class: StorageClass, ciphertext_len: usize) -> bool {
    match storage_class {
        StorageClass::Control => false,
        StorageClass::StatePack => ciphertext_len >= STATE_PACK_RS_THRESHOLD_BYTES,
        StorageClass::Blob => true,
    }
}

/// Selects an adaptive RS profile matching the available eligible peer count.
pub fn select_rs_profile(eligible_peer_count: usize) -> RsProfile {
    match eligible_peer_count {
        0 | 1 => RsProfile {
            data_shards: 1,
            parity_shards: 0,
        },
        2 => RsProfile {
            data_shards: 1,
            parity_shards: 1,
        },
        3..=5 => RsProfile {
            data_shards: 2,
            parity_shards: 1,
        },
        6..=8 => RsProfile {
            data_shards: 4,
            parity_shards: 2,
        },
        _ => RsProfile {
            data_shards: 6,
            parity_shards: 3,
        },
    }
}

/// Shard availability health status.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ShardHealth {
    /// All k + m shards are available and reachable.
    Healthy,
    /// At least one parity shard missing, but still > k shards available.
    Degraded,
    /// Exactly k shards available; any further loss causes permanent data loss.
    Critical,
    /// Fewer than k shards available; data cannot be reconstructed.
    Lost,
}

/// Evaluates health threshold according to canonical spec 11.
pub fn evaluate_shard_health(reachable_shards: usize, profile: RsProfile) -> ShardHealth {
    let total = profile.total_shards();
    let k = profile.data_shards as usize;

    if reachable_shards >= total {
        ShardHealth::Healthy
    } else if reachable_shards > k {
        ShardHealth::Degraded
    } else if reachable_shards == k {
        ShardHealth::Critical
    } else {
        ShardHealth::Lost
    }
}
