//! Distributed Storage Placement, Streaming, Receipts, and Self-Healing Repair.
//!
//! Implements Phase 7 of FORTIQ Canonical Architecture v3:
//! - Eligible Storage Peer capability advertisements
//! - Weighted rendezvous hashing and physical chassis failure-domain placement
//! - Bounded streaming shard transfer protocol (`/fortiq/shard/1`)
//! - Content-addressed custody receipts and dynamic placement records
//! - Empirical retrieval audits and peer reliability scoring
//! - Decentralized self-healing repair and shard regeneration

pub mod audit;
pub mod peer;
pub mod placement;
pub mod receipt;
pub mod repair;
pub mod streaming;

#[cfg(test)]
mod tests;

pub use audit::{AuditSample, PeerAvailabilityTracker, PeerScore};
pub use peer::StoragePeerCapability;
pub use placement::{compute_rendezvous_score, PlacementEngine, RENDEZVOUS_DOMAIN};
pub use receipt::{ShardPlacementRecord, ShardReceipt};
pub use repair::RepairEngine;
pub use streaming::{
    ShardStreamError, ShardStreamFrame, ShardStreamReceiver, MAX_STREAM_CHUNK_BYTES,
    MAX_STREAM_SHARD_BYTES, SHARD_STREAM_PROTOCOL,
};
