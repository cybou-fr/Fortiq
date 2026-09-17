//! Weighted Rendezvous Hashing and Failure-Domain Shard Placement.
//!
//! Hard Invariants & Specifications (docs/spec/11-placement-repair-availability.md):
//! - Weighted rendezvous hashing as a deterministic, distributed candidate selector.
//! - Failure-domain rules: distinct PeerIds, distinct machine identities, avoid
//!   same physical node or site for multiple critical shards of the same stripe.

use crate::canonical::distribution::peer::StoragePeerCapability;
use std::collections::HashSet;

pub const RENDEZVOUS_DOMAIN: &[u8] = b"FORTIQ-RENDEZVOUS-v1:";

/// Computes the deterministic rendezvous score between a shard hash and candidate peer.
pub fn compute_rendezvous_score(shard_hash: &[u8; 32], peer: &StoragePeerCapability) -> u128 {
    let mut hasher = blake3::Hasher::new();
    hasher.update(RENDEZVOUS_DOMAIN);
    hasher.update(shard_hash);
    hasher.update(peer.node_id.as_bytes());

    let digest = hasher.finalize();
    let bytes = digest.as_bytes();

    let base_score = u64::from_le_bytes([
        bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
    ]);

    // Weighted score
    (base_score as u128) * (peer.weight as u128)
}

/// Placement engine selecting optimal peers for shard replicas while obeying failure domains.
pub struct PlacementEngine;

impl PlacementEngine {
    /// Selects `target_count` distinct peers for `shard_hash` enforcing failure domains.
    ///
    /// Existing peers already assigned other shards in the same stripe can be passed
    /// in `existing_stripe_machines` to guarantee physical chassis isolation.
    pub fn select_peers<'a>(
        shard_hash: &[u8; 32],
        shard_size_bytes: u64,
        candidates: &'a [StoragePeerCapability],
        target_count: usize,
        existing_stripe_machines: &HashSet<[u8; 16]>,
    ) -> Vec<&'a StoragePeerCapability> {
        let mut eligible: Vec<(&'a StoragePeerCapability, u128)> = candidates
            .iter()
            .filter(|p| p.can_store(shard_size_bytes))
            .map(|p| (p, compute_rendezvous_score(shard_hash, p)))
            .collect();

        // Sort descending by rendezvous score
        eligible.sort_by_key(|b| std::cmp::Reverse(b.1));

        let mut selected = Vec::with_capacity(target_count);
        let mut used_nodes = HashSet::new();
        let mut used_peer_ids = HashSet::new();
        let mut used_machines = existing_stripe_machines.clone();

        for (peer, _) in eligible {
            if selected.len() >= target_count {
                break;
            }

            // Failure domain rule 1: distinct node_id
            if used_nodes.contains(&peer.node_id) {
                continue;
            }

            // Failure domain rule 2: distinct transport peer_id
            if used_peer_ids.contains(&peer.peer_id) {
                continue;
            }

            // Failure domain rule 3: distinct physical machine_id
            if used_machines.contains(&peer.machine_id) {
                continue;
            }

            used_nodes.insert(peer.node_id);
            used_peer_ids.insert(peer.peer_id.clone());
            used_machines.insert(peer.machine_id);
            selected.push(peer);
        }

        selected
    }
}
