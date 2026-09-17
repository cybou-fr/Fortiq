//! Encrypted Ticket/Segment Snapshots for Fast Cold Start.
//!
//! Specifications (docs/spec/19-snapshots-search-cold-start.md):
//! - Snapshots optimize cold start without replacing history.
//! - A snapshot records reducer version, incorporated frontier heads, and materialized state.
//! - The reducer validates the snapshot frontier and applies only tail events.
//! - Tail events always win according to reducer rules.

use crate::canonical::events::graph::EventGraph;
use crate::canonical::events::reducer::{
    AttachmentView, ChatMessageView, RoleResolver, TicketView,
};
use crate::canonical::records::LogicalEvent;
use crate::canonical::types::{ObjectId, TicketId};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

/// Current reducer schema version.
pub const TICKET_REDUCER_VERSION: u16 = 2;

/// Serializable point-in-time materialized state snapshot of a ticket.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketSnapshot {
    pub schema_version: u16,
    pub ticket_id: TicketId,
    pub reducer_version: u16,
    pub frontier_head_packs: Vec<ObjectId>,
    pub materialized_state: TicketView,
    pub created_at: u64,
}

impl TicketSnapshot {
    /// Creates a new snapshot from an existing materialized `TicketView`.
    pub fn create(view: &TicketView, created_at: u64) -> Self {
        Self {
            schema_version: 1,
            ticket_id: view.ticket_id,
            reducer_version: TICKET_REDUCER_VERSION,
            frontier_head_packs: view.incorporated_packs.clone(),
            materialized_state: view.clone(),
            created_at,
        }
    }
}

/// Fast cold-start reducer: begins with a verified snapshot and applies only tail events.
pub fn reduce_ticket_from_snapshot(
    snapshot: &TicketSnapshot,
    graph: &EventGraph,
    resolver: &impl RoleResolver,
) -> TicketView {
    let mut view = snapshot.materialized_state.clone();
    let incorporated: HashSet<ObjectId> = snapshot.frontier_head_packs.iter().copied().collect();

    let all_pack_ids = graph.get_ticket_packs(&snapshot.ticket_id);

    for &pack_id in all_pack_ids {
        // Skip already incorporated packs in the snapshot
        if incorporated.contains(&pack_id) {
            continue;
        }

        // Exclude tombstoned packs
        if graph.is_tombstoned(&pack_id) {
            continue;
        }

        let plaintext = match graph.get_plaintext(&pack_id) {
            Some(p) => p,
            None => continue,
        };

        // Determine author role from the writer key id (fail-closed)
        let role = match graph
            .get_object(&pack_id)
            .and_then(|obj| resolver.resolve_role(&obj.tbs.writer_key_id))
        {
            Some(r) => r,
            None => continue,
        };

        // Apply tail events on top of the snapshot
        for event in &plaintext.events {
            match event {
                LogicalEvent::ChatMessage { seq, body, .. } => {
                    view.messages.push(ChatMessageView {
                        pack_id,
                        seq: *seq,
                        body: body.clone(),
                        edit_history: Vec::new(),
                    });
                }
                LogicalEvent::ChatMessageRevised {
                    original_seq,
                    replacement_body,
                    ..
                } => {
                    if let Some(msg) = view.messages.iter_mut().find(|m| m.seq == *original_seq) {
                        let old_body = std::mem::replace(&mut msg.body, replacement_body.clone());
                        msg.edit_history.push(old_body);
                    }
                }
                LogicalEvent::FileAttached {
                    blob_id,
                    filename,
                    size_bytes,
                    ..
                } => {
                    view.attachments.push(AttachmentView {
                        pack_id,
                        blob_id: *blob_id,
                        filename: filename.clone(),
                        size_bytes: *size_bytes,
                    });
                }
                LogicalEvent::TicketStateChanged { .. } => {
                    view.safety.apply_transition(role, event);
                }
                LogicalEvent::TicketCreated { .. } => {}
            }
        }

        view.incorporated_packs.push(pack_id);
    }

    view
}
