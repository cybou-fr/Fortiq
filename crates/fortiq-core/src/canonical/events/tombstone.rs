//! Logical deletion (Tombstone) and conflict resolution (CanonicalHeadSet).
//!
//! Hard Invariants:
//! - Invariant 13: Admin conflict resolution via `CanonicalHeadSet` allows
//!   presentation/business state arbitration, but MUST NOT grant shell access
//!   that a client-local safety state has revoked.
//! - Tombstones logically exclude objects from active reducer views without
//!   prematurely deleting the tombstone evidence (anti-resurrection).

use crate::canonical::types::{EntityId, ObjectId, TicketId};
use serde::{Deserialize, Serialize};

/// Logical deletion marker for an object in the event graph.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Tombstone {
    pub target_object_id: ObjectId,
    pub deleted_by: EntityId,
    pub reason: String,
    pub deleted_at: u64,
}

impl Tombstone {
    pub fn new(
        target_object_id: ObjectId,
        deleted_by: EntityId,
        reason: impl Into<String>,
        deleted_at: u64,
    ) -> Self {
        Self {
            target_object_id,
            deleted_by,
            reason: reason.into(),
            deleted_at,
        }
    }
}

/// Explicit frontier head selection authored by Admin/Owner to resolve
/// concurrent branches for presentation and business state.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CanonicalHeadSet {
    pub ticket_id: TicketId,
    pub canonical_heads: Vec<ObjectId>,
    pub resolved_by: EntityId,
    pub resolved_at: u64,
}

impl CanonicalHeadSet {
    pub fn new(
        ticket_id: TicketId,
        canonical_heads: Vec<ObjectId>,
        resolved_by: EntityId,
        resolved_at: u64,
    ) -> Self {
        Self {
            ticket_id,
            canonical_heads,
            resolved_by,
            resolved_at,
        }
    }
}
