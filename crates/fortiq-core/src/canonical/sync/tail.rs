//! Tail Sync Protocol and Graph Traversal.
//!
//! Hard Invariants & Specifications (docs/spec/12-sync-heads-anti-entropy.md):
//! - Peer compares known head with remote head.
//! - If different:
//!   - walk `prev_pack_id` backwards until reaching a known PackId.
//!   - fetch missing packs/manifests.
//!   - reduce forward in topological order.
//! - Fork detection: Detect divergent chains originating from identical sequence points.

use crate::canonical::types::{ObjectId, StreamId};
use std::collections::HashSet;
use thiserror::Error;

#[derive(Error, Debug, PartialEq, Eq)]
pub enum TailSyncError {
    #[error("remote pack {0} could not be resolved or fetched")]
    FetchFailed(ObjectId),
    #[error("stream fork detected on stream {0}: remote pack {1} diverges from local pack {2}")]
    StreamForkDetected(StreamId, ObjectId, ObjectId),
    #[error("backward traversal exceeded maximum allowed depth {0}")]
    TraversalDepthExceeded(usize),
}

/// Status of tail synchronization comparison.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TailSyncStatus {
    UpToDate,
    NeedsCatchUp {
        remote_seq: u64,
        local_seq: u64,
        missing_packs_forward: Vec<ObjectId>,
    },
}

/// Metadata needed to step backward along a writer stream pack history.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PackHeaderInfo {
    pub pack_id: ObjectId,
    pub stream_id: StreamId,
    pub writer_seq: u64,
    pub prev_pack_id: Option<ObjectId>,
}

/// Planner for tail synchronization backwards walk and forward reduction order.
pub struct TailSyncPlanner {
    max_traversal_depth: usize,
}

impl Default for TailSyncPlanner {
    fn default() -> Self {
        Self::new(10_000)
    }
}

impl TailSyncPlanner {
    pub fn new(max_traversal_depth: usize) -> Self {
        Self {
            max_traversal_depth,
        }
    }

    /// Computes the forward list of missing packs by walking backward from `remote_head_pack`
    /// until hitting a pack known to the `is_locally_known` predicate.
    ///
    /// The `fetch_header_fn` provides the header information (`prev_pack_id`, `writer_seq`)
    /// for any given pack ID.
    pub fn plan_backward_walk<F, K>(
        &self,
        remote_head_pack: ObjectId,
        mut fetch_header_fn: F,
        mut is_locally_known: K,
    ) -> Result<Vec<ObjectId>, TailSyncError>
    where
        F: FnMut(ObjectId) -> Option<PackHeaderInfo>,
        K: FnMut(ObjectId) -> bool,
    {
        if is_locally_known(remote_head_pack) {
            return Ok(Vec::new());
        }

        let mut collected_reverse = Vec::new();
        let mut visited = HashSet::new();
        let mut current_id = remote_head_pack;

        loop {
            if visited.len() >= self.max_traversal_depth {
                return Err(TailSyncError::TraversalDepthExceeded(
                    self.max_traversal_depth,
                ));
            }

            if !visited.insert(current_id) {
                // Cycle detected in prev links
                return Err(TailSyncError::TraversalDepthExceeded(visited.len()));
            }

            collected_reverse.push(current_id);

            let header =
                fetch_header_fn(current_id).ok_or(TailSyncError::FetchFailed(current_id))?;

            match header.prev_pack_id {
                Some(prev_id) => {
                    if is_locally_known(prev_id) {
                        // Reached known common ancestor!
                        break;
                    }
                    current_id = prev_id;
                }
                None => {
                    // Reached genesis pack of this writer stream
                    break;
                }
            }
        }

        // Reverse to get topological order: oldest missing pack first -> newest last
        collected_reverse.reverse();
        Ok(collected_reverse)
    }

    /// Compares a known local stream head against a remote advertisement
    /// and generates a tail synchronization plan.
    pub fn plan_tail_sync<F, K>(
        &self,
        stream_id: StreamId,
        local_head: Option<(u64, ObjectId)>,
        remote_head: (u64, ObjectId),
        fetch_header_fn: F,
        is_locally_known: K,
    ) -> Result<TailSyncStatus, TailSyncError>
    where
        F: FnMut(ObjectId) -> Option<PackHeaderInfo>,
        K: FnMut(ObjectId) -> bool,
    {
        let (remote_seq, remote_pack) = remote_head;

        if let Some((local_seq, local_pack)) = local_head {
            if local_pack == remote_pack && local_seq == remote_seq {
                return Ok(TailSyncStatus::UpToDate);
            }

            if remote_seq <= local_seq && local_pack != remote_pack {
                // Remote claims same or lower sequence with a divergent pack ID!
                return Err(TailSyncError::StreamForkDetected(
                    stream_id,
                    remote_pack,
                    local_pack,
                ));
            }

            let missing_packs =
                self.plan_backward_walk(remote_pack, fetch_header_fn, is_locally_known)?;

            Ok(TailSyncStatus::NeedsCatchUp {
                remote_seq,
                local_seq,
                missing_packs_forward: missing_packs,
            })
        } else {
            // We have no local packs for this stream at all
            let missing_packs =
                self.plan_backward_walk(remote_pack, fetch_header_fn, is_locally_known)?;

            Ok(TailSyncStatus::NeedsCatchUp {
                remote_seq,
                local_seq: 0,
                missing_packs_forward: missing_packs,
            })
        }
    }
}
