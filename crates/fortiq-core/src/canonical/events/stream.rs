//! Writer stream append chain and fork detection.
//!
//! Every authoring session writes to an append-only stream with monotonic
//! sequence numbers and strict previous-pack hash chaining:
//! `Pack N: prev_pack_id = Pack N-1, stream_id, writer_seq`.
//! A fork is detected if the same stream emits competing successors.

use crate::canonical::types::{ObjectId, StreamId};
use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum StreamError {
    #[error("Stream fork detected on stream {stream_id}: expected seq {expected_seq} with prev {expected_prev:?}, got seq {got_seq} with prev {got_prev:?}")]
    ForkDetected {
        stream_id: StreamId,
        expected_seq: u64,
        expected_prev: Option<ObjectId>,
        got_seq: u64,
        got_prev: Option<ObjectId>,
    },
    #[error("Sequence gap in stream {stream_id}: expected seq {expected_seq}, got seq {got_seq}")]
    SequenceGap {
        stream_id: StreamId,
        expected_seq: u64,
        got_seq: u64,
    },
    #[error("Invalid genesis append on stream {stream_id}: prev_pack_id must be None for seq 1, got {got_prev:?}")]
    InvalidGenesis {
        stream_id: StreamId,
        got_prev: Option<ObjectId>,
    },
}

/// An immutable append entry in a writer stream chain.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StreamAppendEntry {
    pub stream_id: StreamId,
    pub seq: u64,
    pub prev_pack_id: Option<ObjectId>,
    pub pack_id: ObjectId,
}

/// Tracking cursor for a single writer stream.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StreamCursor {
    pub stream_id: StreamId,
    pub head_seq: u64,
    pub head_pack_id: Option<ObjectId>,
}

impl StreamCursor {
    /// Creates a new cursor for an uninitialized writer stream.
    pub const fn new(stream_id: StreamId) -> Self {
        Self {
            stream_id,
            head_seq: 0,
            head_pack_id: None,
        }
    }

    /// Validates whether an incoming pack can be appended to this cursor.
    pub fn validate_next(
        &self,
        seq: u64,
        prev_pack_id: Option<ObjectId>,
    ) -> Result<(), StreamError> {
        let expected_seq = self.head_seq + 1;
        if seq == 0 {
            return Err(StreamError::SequenceGap {
                stream_id: self.stream_id,
                expected_seq,
                got_seq: 0,
            });
        }

        if seq < expected_seq {
            return Err(StreamError::ForkDetected {
                stream_id: self.stream_id,
                expected_seq,
                expected_prev: self.head_pack_id,
                got_seq: seq,
                got_prev: prev_pack_id,
            });
        }

        if seq > expected_seq {
            return Err(StreamError::SequenceGap {
                stream_id: self.stream_id,
                expected_seq,
                got_seq: seq,
            });
        }

        // seq == expected_seq
        if self.head_seq == 0 {
            if prev_pack_id.is_some() {
                return Err(StreamError::InvalidGenesis {
                    stream_id: self.stream_id,
                    got_prev: prev_pack_id,
                });
            }
        } else if prev_pack_id != self.head_pack_id {
            return Err(StreamError::ForkDetected {
                stream_id: self.stream_id,
                expected_seq,
                expected_prev: self.head_pack_id,
                got_seq: seq,
                got_prev: prev_pack_id,
            });
        }

        Ok(())
    }

    /// Advances the cursor with the newly verified pack.
    pub fn advance(&mut self, pack_id: ObjectId) -> StreamAppendEntry {
        self.head_seq += 1;
        let prev = self.head_pack_id;
        self.head_pack_id = Some(pack_id);

        StreamAppendEntry {
            stream_id: self.stream_id,
            seq: self.head_seq,
            prev_pack_id: prev,
            pack_id,
        }
    }

    /// Validates and immediately advances with an incoming append.
    pub fn accept_append(
        &mut self,
        seq: u64,
        prev_pack_id: Option<ObjectId>,
        pack_id: ObjectId,
    ) -> Result<StreamAppendEntry, StreamError> {
        self.validate_next(seq, prev_pack_id)?;
        Ok(self.advance(pack_id))
    }
}
