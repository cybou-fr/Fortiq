//! Shard Receipts and Placement Records.
//!
//! Hard Invariants & Specifications (ADR-006, docs/spec/11-placement-repair-availability.md):
//! - Content manifests define content and shards; Placement/receipts define current holders.
//! - Repair or migration moves shards without changing BlobManifest or ObjectId.
//! - A receipt proves that a peer acknowledged storage at that timestamp.

use crate::canonical::codec::serde_bytes;
use crate::canonical::types::{BlobId, EntityId};
use serde::{Deserialize, Serialize};

/// Cryptographic receipt signed by a storing peer acknowledging custody of a shard.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShardReceipt {
    pub blob_id: BlobId,
    pub stripe_index: u32,
    pub shard_index: u8,
    pub shard_hash: [u8; 32],
    pub storing_peer: EntityId,
    pub storing_peer_id: String,
    pub acknowledged_bytes: u64,
    pub timestamp: u64,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

impl ShardReceipt {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        blob_id: BlobId,
        stripe_index: u32,
        shard_index: u8,
        shard_hash: [u8; 32],
        storing_peer: EntityId,
        storing_peer_id: impl Into<String>,
        acknowledged_bytes: u64,
        timestamp: u64,
        signature: Vec<u8>,
    ) -> Self {
        Self {
            blob_id,
            stripe_index,
            shard_index,
            shard_hash,
            storing_peer,
            storing_peer_id: storing_peer_id.into(),
            acknowledged_bytes,
            timestamp,
            signature,
        }
    }
}

/// Dynamic placement record tracking current holders of a shard.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShardPlacementRecord {
    pub blob_id: BlobId,
    pub stripe_index: u32,
    pub shard_index: u8,
    pub shard_hash: [u8; 32],
    pub receipts: Vec<ShardReceipt>,
}

impl ShardPlacementRecord {
    pub fn new(blob_id: BlobId, stripe_index: u32, shard_index: u8, shard_hash: [u8; 32]) -> Self {
        Self {
            blob_id,
            stripe_index,
            shard_index,
            shard_hash,
            receipts: Vec::new(),
        }
    }

    /// Records or updates custody receipt from a peer.
    pub fn add_receipt(&mut self, receipt: ShardReceipt) {
        if let Some(pos) = self
            .receipts
            .iter()
            .position(|r| r.storing_peer == receipt.storing_peer)
        {
            self.receipts[pos] = receipt;
        } else {
            self.receipts.push(receipt);
        }
    }

    /// Returns the number of distinct peers holding verified receipts for this shard.
    pub fn holder_count(&self) -> usize {
        self.receipts.len()
    }
}
