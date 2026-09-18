//! Deterministic Ticket State Reducer.
//!
//! Reconstructs full ticket state (lifecycle, messages, attachments, and shell safety)
//! from immutable EventPacks in the Event Graph.
//! Respects Tombstones (logical deletion) and CanonicalHeadSets (admin conflict resolution).

use crate::canonical::events::graph::EventGraph;
use crate::canonical::records::LogicalEvent;
use crate::canonical::types::{BlobId, ObjectId, TicketId};
use crate::TicketState;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

/// View of a chat message in the ticket timeline.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChatMessageView {
    pub pack_id: ObjectId,
    pub seq: u64,
    pub body: String,
    #[serde(default)]
    pub edit_history: Vec<String>,
}

/// View of an attachment associated with the ticket.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttachmentView {
    pub pack_id: ObjectId,
    pub blob_id: BlobId,
    pub filename: String,
    pub size_bytes: u64,
}

/// Consolidated materialized view of a support ticket.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketView {
    pub ticket_id: TicketId,
    pub title: String,
    pub state: TicketState,
    pub messages: Vec<ChatMessageView>,
    pub attachments: Vec<AttachmentView>,
    pub incorporated_packs: Vec<ObjectId>,
}

/// Deterministically reduces all accepted events for `ticket_id` from the `EventGraph`.
pub fn reduce_ticket(ticket_id: TicketId, graph: &EventGraph) -> Option<TicketView> {
    let all_pack_ids = graph.get_ticket_packs(&ticket_id);
    if all_pack_ids.is_empty() {
        return None;
    }

    // Optional admin canonical head filter walking backward to include full ancestry
    let head_set_filter: Option<HashSet<ObjectId>> =
        graph.get_canonical_heads(&ticket_id).map(|heads| {
            let mut allowed = HashSet::new();
            for head in &heads.canonical_heads {
                allowed.extend(graph.get_pack_ancestors_inclusive(head));
            }
            allowed
        });

    let mut view: Option<TicketView> = None;

    for &pack_id in all_pack_ids {
        // Exclude tombstoned packs
        if graph.is_tombstoned(&pack_id) {
            continue;
        }

        // If CanonicalHeadSet is set, ensure pack is part of the designated head set or its ancestry
        if let Some(ref heads) = head_set_filter {
            if !heads.contains(&pack_id) {
                continue;
            }
        }

        let plaintext = match graph.get_plaintext(&pack_id) {
            Some(p) => p,
            None => continue,
        };

        for event in &plaintext.events {
            match event {
                LogicalEvent::TicketCreated { title, .. } => {
                    view = Some(TicketView {
                        ticket_id,
                        title: title.clone(),
                        state: TicketState::Open,
                        messages: Vec::new(),
                        attachments: Vec::new(),
                        incorporated_packs: Vec::new(),
                    });
                }
                LogicalEvent::ChatMessage { seq, body, .. } => {
                    if let Some(v) = &mut view {
                        v.messages.push(ChatMessageView {
                            pack_id,
                            seq: *seq,
                            body: body.clone(),
                            edit_history: Vec::new(),
                        });
                    }
                }
                LogicalEvent::ChatMessageRevised {
                    original_seq,
                    replacement_body,
                    ..
                } => {
                    if let Some(v) = &mut view {
                        if let Some(msg) = v.messages.iter_mut().find(|m| m.seq == *original_seq) {
                            let old_body =
                                std::mem::replace(&mut msg.body, replacement_body.clone());
                            msg.edit_history.push(old_body);
                        }
                    }
                }
                LogicalEvent::FileAttached {
                    blob_id,
                    filename,
                    size_bytes,
                    ..
                } => {
                    if let Some(v) = &mut view {
                        v.attachments.push(AttachmentView {
                            pack_id,
                            blob_id: *blob_id,
                            filename: filename.clone(),
                            size_bytes: *size_bytes,
                        });
                    }
                }
                LogicalEvent::TicketStateChanged { .. } => {
                    if let Some(v) = &mut view {
                        if let LogicalEvent::TicketStateChanged { state, .. } = event {
                            if v.state.can_transition_to(*state) {
                                v.state = *state;
                            }
                        }
                    }
                }
            }
        }

        if let Some(v) = &mut view {
            v.incorporated_packs.push(pack_id);
        }
    }

    view
}
