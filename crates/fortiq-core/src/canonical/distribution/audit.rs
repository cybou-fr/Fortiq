//! Shard Retrieval Audits and Peer Availability Scoring.
//!
//! Specifications (docs/spec/11-placement-repair-availability.md):
//! - A receipt proves only initial custody, not ongoing possession.
//! - Background audits sample shard retrieval to calculate true empirical availability.
//! - Underperforming peers are marked degraded and trigger proactive repair.

use crate::canonical::types::EntityId;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Result of a single retrieval challenge/audit.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AuditSample {
    pub peer: EntityId,
    pub shard_hash: [u8; 32],
    pub timestamp: u64,
    pub success: bool,
    pub latency_ms: u32,
}

/// Dynamic score tracking empirical retrieval reliability of a storage peer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PeerScore {
    pub peer: EntityId,
    pub total_audits: u32,
    pub successful_audits: u32,
    pub failed_audits: u32,
}

impl PeerScore {
    pub fn new(peer: EntityId) -> Self {
        Self {
            peer,
            total_audits: 0,
            successful_audits: 0,
            failed_audits: 0,
        }
    }

    /// Records an audit result.
    pub fn record(&mut self, success: bool) {
        self.total_audits += 1;
        if success {
            self.successful_audits += 1;
        } else {
            self.failed_audits += 1;
        }
    }

    /// Empirical availability percentage [0, 100]. Returns 100 if untested.
    pub fn availability_percentage(&self) -> u32 {
        if self.total_audits == 0 {
            100
        } else {
            ((self.successful_audits as u64 * 100) / (self.total_audits as u64)) as u32
        }
    }

    /// Returns whether the peer is considered reliable (>= 90% audit pass rate).
    pub fn is_reliable(&self) -> bool {
        self.availability_percentage() >= 90
    }
}

/// Global tracking engine for peer storage availability.
#[derive(Default, Debug, Clone)]
pub struct PeerAvailabilityTracker {
    scores: HashMap<EntityId, PeerScore>,
}

impl PeerAvailabilityTracker {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn record_sample(&mut self, sample: AuditSample) {
        let entry = self
            .scores
            .entry(sample.peer)
            .or_insert_with(|| PeerScore::new(sample.peer));
        entry.record(sample.success);
    }

    pub fn get_score(&self, peer: &EntityId) -> Option<&PeerScore> {
        self.scores.get(peer)
    }

    pub fn is_peer_reliable(&self, peer: &EntityId) -> bool {
        self.scores
            .get(peer)
            .map(|s| s.is_reliable())
            .unwrap_or(true)
    }
}
