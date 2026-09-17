//! File Upload Resumption and Session State.
//!
//! Hard Invariants & Specifications (docs/spec/13-chat-and-files.md):
//! - Resumption is keyed by AttachmentId, stripe index, and shard availability.
//! - Invariant: Never re-encrypt an already committed chunk with the same FileKey and a different plaintext under the same nonce.
//! - Tracks committed stripes and storage receipts.

use crate::canonical::distribution::receipt::ShardReceipt;
use crate::canonical::types::{BlobId, ObjectId};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use thiserror::Error;

#[derive(Error, Debug, PartialEq, Eq)]
pub enum ResumeError {
    #[error("stripe index {0} out of bounds (total stripes: {1})")]
    StripeIndexOutOfBounds(u32, u32),
    #[error("stripe index {0} already committed with different checksum")]
    StripeConflict(u32),
}

/// Tracks the upload and distributed placement progress of a file attachment.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileUploadSession {
    pub attachment_id: ObjectId,
    pub blob_id: BlobId,
    pub total_bytes: u64,
    pub stripe_size: u32,
    pub total_stripes: u32,
    pub committed_stripes: HashSet<u32>,
    pub receipts: HashMap<u32, Vec<ShardReceipt>>,
}

impl FileUploadSession {
    /// Creates a new upload session for an attachment.
    pub fn new(
        attachment_id: ObjectId,
        blob_id: BlobId,
        total_bytes: u64,
        stripe_size: u32,
    ) -> Self {
        let sz = stripe_size.max(1) as u64;
        let total_stripes = if total_bytes == 0 {
            1
        } else {
            total_bytes.div_ceil(sz) as u32
        };

        Self {
            attachment_id,
            blob_id,
            total_bytes,
            stripe_size,
            total_stripes,
            committed_stripes: HashSet::new(),
            receipts: HashMap::new(),
        }
    }

    /// Records that a stripe has been fully transferred and accepted by storage peers.
    pub fn record_stripe_committed(
        &mut self,
        stripe_index: u32,
        stripe_receipts: Vec<ShardReceipt>,
    ) -> Result<(), ResumeError> {
        if stripe_index >= self.total_stripes {
            return Err(ResumeError::StripeIndexOutOfBounds(
                stripe_index,
                self.total_stripes,
            ));
        }

        self.committed_stripes.insert(stripe_index);
        self.receipts.insert(stripe_index, stripe_receipts);
        Ok(())
    }

    /// Checks if a specific stripe has already been committed.
    pub fn is_stripe_committed(&self, stripe_index: u32) -> bool {
        self.committed_stripes.contains(&stripe_index)
    }

    /// Returns the sorted list of stripe indices that still need to be uploaded.
    pub fn pending_stripes(&self) -> Vec<u32> {
        (0..self.total_stripes)
            .filter(|idx| !self.committed_stripes.contains(idx))
            .collect()
    }

    /// Returns true if all stripes for this file have been successfully committed.
    pub fn is_complete(&self) -> bool {
        self.committed_stripes.len() == self.total_stripes as usize
    }

    /// Returns the fractional progress [0.0, 1.0] of committed stripes.
    pub fn progress_fraction(&self) -> f64 {
        if self.total_stripes == 0 {
            1.0
        } else {
            self.committed_stripes.len() as f64 / self.total_stripes as f64
        }
    }
}
