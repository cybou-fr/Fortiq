//! Head Advertisements and Stream Head Tracking.
//!
//! Hard Invariants & Specifications (docs/spec/12-sync-heads-anti-entropy.md, docs/spec/17-protocol-map-resource-limits.md):
//! - Each writer stream has a latest PackId.
//! - A signed HeadAdvertisement is routing state, not canonical application state, and may be replaced frequently.
//! - Monotonically increasing sequence numbers: newer sequence numbers replace older ones.
//! - Equivocation/fork detection: identical sequence numbers with differing PackIds indicate a stream fork.
//! - Protocol limit: Head advertisement <= 16 KiB.

use crate::canonical::codec::serde_bytes;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{KeyId, NetworkId, ObjectId, SegmentId, StreamId};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use thiserror::Error;

/// Domain separation context for Head Advertisement signing.
pub const HEAD_ADV_SIG_DOMAIN: &[u8] = b"FORTIQ_HEAD_ADVERTISEMENT_V1";

/// Maximum allowed head advertisement size (16 KiB).
pub const MAX_HEAD_ADV_BYTES: usize = 16 * 1024;

#[derive(Error, Debug, PartialEq, Eq)]
pub enum HeadError {
    #[error("head advertisement expired at {0}, current time {1}")]
    Expired(u64, u64),
    #[error("cryptographic signing error: {0}")]
    Signing(String),
    #[error("stream fork / equivocation detected for stream {0} at seq {1}")]
    ForkDetected(StreamId, u64),
    #[error("stale head advertisement: received seq {0} <= current seq {1}")]
    StaleSequence(u64, u64),
}

impl From<SigningError> for HeadError {
    fn from(e: SigningError) -> Self {
        HeadError::Signing(e.to_string())
    }
}

/// Bounded, signed advertisement of the latest head of a writer stream.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HeadAdvertisement {
    pub network_id: NetworkId,
    pub segment_id: SegmentId,
    pub writer_key_id: KeyId,
    pub writer_stream_id: StreamId,
    pub writer_seq: u64,
    pub head_pack_id: ObjectId,
    pub expires_at: u64,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

impl HeadAdvertisement {
    /// Constructs the domain-separated bytes to be signed.
    pub fn compute_signing_payload(
        network_id: &NetworkId,
        segment_id: &SegmentId,
        writer_key_id: &KeyId,
        writer_stream_id: &StreamId,
        writer_seq: u64,
        head_pack_id: &ObjectId,
        expires_at: u64,
    ) -> Vec<u8> {
        let mut payload =
            Vec::with_capacity(HEAD_ADV_SIG_DOMAIN.len() + 32 + 32 + 32 + 16 + 8 + 32 + 8);
        payload.extend_from_slice(HEAD_ADV_SIG_DOMAIN);
        payload.extend_from_slice(network_id.as_bytes());
        payload.extend_from_slice(segment_id.as_bytes());
        payload.extend_from_slice(writer_key_id.as_bytes());
        payload.extend_from_slice(writer_stream_id.as_bytes());
        payload.extend_from_slice(&writer_seq.to_be_bytes());
        payload.extend_from_slice(head_pack_id.as_bytes());
        payload.extend_from_slice(&expires_at.to_be_bytes());
        payload
    }

    /// Creates and signs a new `HeadAdvertisement`.
    pub fn create_and_sign(
        network_id: NetworkId,
        segment_id: SegmentId,
        writer_stream_id: StreamId,
        writer_seq: u64,
        head_pack_id: ObjectId,
        expires_at: u64,
        signer: &impl Signer,
    ) -> Result<Self, HeadError> {
        let writer_key_id = signer.key_id();
        let payload = Self::compute_signing_payload(
            &network_id,
            &segment_id,
            &writer_key_id,
            &writer_stream_id,
            writer_seq,
            &head_pack_id,
            expires_at,
        );
        let signature = signer.sign(&payload)?;
        Ok(Self {
            network_id,
            segment_id,
            writer_key_id,
            writer_stream_id,
            writer_seq,
            head_pack_id,
            expires_at,
            signature,
        })
    }

    /// Verifies the advertisement signature and validates that it is not expired.
    pub fn verify(&self, verifier: &impl Verifier, current_time: u64) -> Result<(), HeadError> {
        if self.expires_at < current_time {
            return Err(HeadError::Expired(self.expires_at, current_time));
        }
        let payload = Self::compute_signing_payload(
            &self.network_id,
            &self.segment_id,
            &self.writer_key_id,
            &self.writer_stream_id,
            self.writer_seq,
            &self.head_pack_id,
            self.expires_at,
        );
        verifier
            .verify(&payload, &self.signature)
            .map_err(|e| HeadError::Signing(e.to_string()))
    }
}

/// In-memory tracker of writer stream heads.
#[derive(Debug, Default, Clone)]
pub struct HeadTracker {
    heads: HashMap<StreamId, HeadAdvertisement>,
}

impl HeadTracker {
    pub fn new() -> Self {
        Self {
            heads: HashMap::new(),
        }
    }

    /// Returns the currently known head advertisement for a stream, if any.
    pub fn get_head(&self, stream_id: &StreamId) -> Option<&HeadAdvertisement> {
        self.heads.get(stream_id)
    }

    /// Updates or inserts a head advertisement.
    ///
    /// Invariant:
    /// - Rejects expired advertisements.
    /// - If incoming sequence > existing sequence: replaces.
    /// - If incoming sequence == existing sequence but different pack ID: detects fork!
    /// - If incoming sequence <= existing sequence with same or older pack ID: rejects as stale.
    pub fn update_head(
        &mut self,
        adv: HeadAdvertisement,
        current_time: u64,
    ) -> Result<(), HeadError> {
        if adv.expires_at < current_time {
            return Err(HeadError::Expired(adv.expires_at, current_time));
        }

        if let Some(existing) = self.heads.get(&adv.writer_stream_id) {
            if adv.writer_seq == existing.writer_seq {
                if adv.head_pack_id != existing.head_pack_id {
                    return Err(HeadError::ForkDetected(
                        adv.writer_stream_id,
                        adv.writer_seq,
                    ));
                }
                // Same sequence and same pack ID, update expires_at if newer
                if adv.expires_at > existing.expires_at {
                    self.heads.insert(adv.writer_stream_id, adv);
                }
                return Ok(());
            }

            if adv.writer_seq < existing.writer_seq {
                return Err(HeadError::StaleSequence(
                    adv.writer_seq,
                    existing.writer_seq,
                ));
            }
        }

        self.heads.insert(adv.writer_stream_id, adv);
        Ok(())
    }

    /// Returns all tracked heads for a given segment.
    pub fn heads_for_segment(&self, segment_id: &SegmentId) -> Vec<HeadAdvertisement> {
        self.heads
            .values()
            .filter(|adv| &adv.segment_id == segment_id)
            .cloned()
            .collect()
    }
}

/// Compact paginated index of head advertisements for a segment.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SegmentHeadIndex {
    pub segment_id: SegmentId,
    pub page_index: u32,
    pub heads: Vec<HeadAdvertisement>,
}
