//! Streaming Shard Transfer Protocol (`/fortiq/shard/1`).
//!
//! Specifications (docs/spec/17-protocol-map-resource-limits.md):
//! - Streams multi-megabyte shards via bounded chunks (SHARD_OPEN, SHARD_DATA*, SHARD_END, ACK).
//! - Incremental hashing via streaming BLAKE3 prevents loading unverified data.
//! - Enforces length limits and verifies hash before emitting custody receipt.

use crate::canonical::codec::serde_bytes;
use crate::canonical::distribution::receipt::ShardReceipt;
use crate::canonical::types::{BlobId, EntityId};
use serde::{Deserialize, Serialize};
use thiserror::Error;

/// Protocol identifier for shard streaming.
pub const SHARD_STREAM_PROTOCOL: &str = "/fortiq/shard/1";

/// Maximum permitted single chunk size (64 KiB) for streaming backpressure.
pub const MAX_STREAM_CHUNK_BYTES: usize = 64 * 1024;

/// Maximum permitted individual shard size (4 MiB).
pub const MAX_STREAM_SHARD_BYTES: u64 = 4 * 1024 * 1024;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum ShardStreamError {
    #[error("Stream not open")]
    NotOpen,
    #[error("Stream already open")]
    AlreadyOpen,
    #[error("Chunk sequence mismatch: expected {expected}, got {got}")]
    SequenceMismatch { expected: u32, got: u32 },
    #[error("Chunk size exceeds limit: {size} > {MAX_STREAM_CHUNK_BYTES}")]
    ChunkTooLarge { size: usize },
    #[error("Total shard size exceeds limit: {size} > {MAX_STREAM_SHARD_BYTES}")]
    ShardTooLarge { size: u64 },
    #[error("Total chunks count mismatch on end: expected {expected}, got {got}")]
    ChunkCountMismatch { expected: u32, got: u32 },
    #[error("Integrity check failed: expected hash {expected:?}, got {got:?}")]
    HashMismatch { expected: [u8; 32], got: [u8; 32] },
}

/// Individual frame transmitted over the `/fortiq/shard/1` streaming substream.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ShardStreamFrame {
    Open {
        blob_id: BlobId,
        stripe_index: u32,
        shard_index: u8,
        expected_hash: [u8; 32],
        total_bytes: u64,
    },
    Data {
        chunk_seq: u32,
        #[serde(with = "serde_bytes")]
        data: Vec<u8>,
    },
    End {
        total_chunks: u32,
    },
    Ack {
        success: bool,
        error: Option<String>,
        receipt: Option<ShardReceipt>,
    },
}

/// State machine receiving and verifying an incoming shard stream incrementally.
pub struct ShardStreamReceiver {
    local_peer_node: EntityId,
    local_transport_peer_id: String,
    open_metadata: Option<(BlobId, u32, u8, [u8; 32], u64)>,
    next_chunk_seq: u32,
    received_bytes: u64,
    hasher: blake3::Hasher,
    buffer: Vec<u8>,
}

impl ShardStreamReceiver {
    pub fn new(local_peer_node: EntityId, local_transport_peer_id: impl Into<String>) -> Self {
        Self {
            local_peer_node,
            local_transport_peer_id: local_transport_peer_id.into(),
            open_metadata: None,
            next_chunk_seq: 0,
            received_bytes: 0,
            hasher: blake3::Hasher::new(),
            buffer: Vec::new(),
        }
    }

    /// Handles an incoming stream frame and returns a response frame if ready.
    pub fn handle_frame(
        &mut self,
        frame: ShardStreamFrame,
        now_ts: u64,
    ) -> Result<Option<ShardStreamFrame>, ShardStreamError> {
        match frame {
            ShardStreamFrame::Open {
                blob_id,
                stripe_index,
                shard_index,
                expected_hash,
                total_bytes,
            } => {
                if self.open_metadata.is_some() {
                    return Err(ShardStreamError::AlreadyOpen);
                }
                if total_bytes > MAX_STREAM_SHARD_BYTES {
                    return Err(ShardStreamError::ShardTooLarge { size: total_bytes });
                }

                self.open_metadata = Some((
                    blob_id,
                    stripe_index,
                    shard_index,
                    expected_hash,
                    total_bytes,
                ));
                self.next_chunk_seq = 0;
                self.received_bytes = 0;
                self.hasher = blake3::Hasher::new();
                self.buffer = Vec::with_capacity(total_bytes as usize);
                Ok(None)
            }
            ShardStreamFrame::Data { chunk_seq, data } => {
                let (_, _, _, _, total_bytes) =
                    self.open_metadata.ok_or(ShardStreamError::NotOpen)?;

                if chunk_seq != self.next_chunk_seq {
                    return Err(ShardStreamError::SequenceMismatch {
                        expected: self.next_chunk_seq,
                        got: chunk_seq,
                    });
                }
                if data.len() > MAX_STREAM_CHUNK_BYTES {
                    return Err(ShardStreamError::ChunkTooLarge { size: data.len() });
                }
                if self.received_bytes + (data.len() as u64) > total_bytes {
                    return Err(ShardStreamError::ShardTooLarge {
                        size: self.received_bytes + (data.len() as u64),
                    });
                }

                self.hasher.update(&data);
                self.received_bytes += data.len() as u64;
                self.buffer.extend_from_slice(&data);
                self.next_chunk_seq += 1;
                Ok(None)
            }
            ShardStreamFrame::End { total_chunks } => {
                let (blob_id, stripe_index, shard_index, expected_hash, total_bytes) =
                    self.open_metadata.take().ok_or(ShardStreamError::NotOpen)?;

                if total_chunks != self.next_chunk_seq {
                    return Err(ShardStreamError::ChunkCountMismatch {
                        expected: self.next_chunk_seq,
                        got: total_chunks,
                    });
                }
                if self.received_bytes != total_bytes {
                    return Err(ShardStreamError::ShardTooLarge {
                        size: self.received_bytes,
                    });
                }

                let computed_hash = *self.hasher.finalize().as_bytes();
                if computed_hash != expected_hash {
                    return Err(ShardStreamError::HashMismatch {
                        expected: expected_hash,
                        got: computed_hash,
                    });
                }

                // Shard verified! Generate receipt
                let receipt = ShardReceipt::new(
                    blob_id,
                    stripe_index,
                    shard_index,
                    computed_hash,
                    self.local_peer_node,
                    self.local_transport_peer_id.clone(),
                    total_bytes,
                    now_ts,
                    vec![0xdd; 64], // simulated peer storage signature
                );

                Ok(Some(ShardStreamFrame::Ack {
                    success: true,
                    error: None,
                    receipt: Some(receipt),
                }))
            }
            ShardStreamFrame::Ack { .. } => Ok(None),
        }
    }

    /// Extracts verified shard bytes upon successful reception.
    pub fn into_buffer(self) -> Vec<u8> {
        self.buffer
    }
}
