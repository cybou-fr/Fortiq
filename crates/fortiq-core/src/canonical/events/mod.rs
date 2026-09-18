//! Local Event Graph, Writer Streams, and Ticket State Reducer.
//!
//! Implements Phase 4 of FORTIQ Canonical Architecture v3:
//! - Writer stream sequencing and fork detection
//! - EventPack batcher and immediate safety flushes
//! - Client-owned TicketAccessEpoch and local safety guard
//! - Immutable append-only EventGraph storage
//! - Materialized Ticket state reducer with Tombstone and CanonicalHeadSet support

pub mod batcher;
pub mod graph;
pub mod reducer;
pub mod search;
pub mod snapshot;
pub mod stream;
pub mod tombstone;

// v3 epoch-oriented tests were superseded by security_audit_phase15_v4.

pub use batcher::{BatchPolicy, EventPackBatcher, FlushDecision};
pub use graph::{EventGraph, EventGraphError, VerifiedEventPack};
pub use reducer::{reduce_ticket, AttachmentView, ChatMessageView, TicketView};
pub use search::{LocalSearchIndex, MatchType, SearchResult};
pub use snapshot::{reduce_ticket_from_snapshot, TicketSnapshot, TICKET_REDUCER_VERSION};
pub use stream::{StreamAppendEntry, StreamCursor, StreamError};
pub use tombstone::{CanonicalHeadSet, Tombstone};
