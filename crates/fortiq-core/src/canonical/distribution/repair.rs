//! Decentralized Shard Repair Engine and Re-encoding Pipeline.
//!
//! Specifications (docs/spec/11-placement-repair-availability.md):
//! - Monitors degraded stripes (k <= reachable < k+m).
//! - Reconstructs original data using any k valid surviving shards.
//! - Re-encodes and regenerates the missing shards.
//! - Places newly generated shards onto fresh eligible peers selected by rendezvous placement.
//! - Duplicate concurrent repair is harmless because shard identity is content-addressed.

use crate::canonical::storage::erasure::{ErasureCoder, ErasureError, Shard};
use crate::canonical::storage::policy::{evaluate_shard_health, ShardHealth};
use crate::canonical::types::RsProfile;

/// Decentralized repair engine for self-healing distributed storage.
pub struct RepairEngine;

impl RepairEngine {
    /// Determines whether a stripe requires active repair based on surviving shard count.
    pub fn needs_repair(reachable_shards: usize, profile: RsProfile) -> bool {
        let health = evaluate_shard_health(reachable_shards, profile);
        matches!(health, ShardHealth::Degraded | ShardHealth::Critical)
    }

    /// Reconstructs the original stripe from surviving shards and regenerates
    /// the requested missing shards.
    pub fn regenerate_missing_shards(
        coder: &dyn ErasureCoder,
        surviving_shards: &[Option<Shard>],
        original_stripe_len: usize,
        profile: RsProfile,
        missing_indices: &[u8],
    ) -> Result<Vec<Shard>, ErasureError> {
        // Step 1: Reconstruct original stripe bytes from any >= k surviving shards
        let reconstructed = coder.reconstruct(surviving_shards, original_stripe_len, profile)?;

        // Step 2: Re-encode full stripe to obtain all shards
        let all_fresh_shards = coder.encode(&reconstructed, profile)?;

        // Step 3: Extract only the missing shards
        let mut regenerated = Vec::new();
        for &missing_idx in missing_indices {
            if let Some(shard) = all_fresh_shards.iter().find(|s| s.index == missing_idx) {
                regenerated.push(shard.clone());
            }
        }

        Ok(regenerated)
    }
}
