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
pub mod safety;
pub mod search;
pub mod snapshot;
pub mod stream;
pub mod tombstone;

#[cfg(test)]
mod tests;

pub use batcher::{BatchPolicy, EventPackBatcher, FlushDecision};
pub use graph::{EventGraph, EventGraphError};
pub use reducer::{
    reduce_ticket, reduce_ticket_with_resolver, AttachmentView, ChatMessageView,
    DefaultRoleResolver, RoleResolver, SimpleRoleResolver, TicketView,
};
pub use safety::{AuthorRole, TicketLifecycle, TicketSafetyState};
pub use search::{LocalSearchIndex, MatchType, SearchResult};
pub use snapshot::{reduce_ticket_from_snapshot, TicketSnapshot, TICKET_REDUCER_VERSION};
pub use stream::{StreamAppendEntry, StreamCursor, StreamError};
pub use tombstone::{CanonicalHeadSet, Tombstone};
