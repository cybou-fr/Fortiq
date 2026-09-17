//! Deterministic Ticket State Reducer.
//!
//! Reconstructs full ticket state (lifecycle, messages, attachments, and shell safety)
//! from immutable EventPacks in the Event Graph.
//! Respects Tombstones (logical deletion) and CanonicalHeadSets (admin conflict resolution).

use crate::canonical::events::graph::EventGraph;
use crate::canonical::events::safety::{AuthorRole, TicketSafetyState};
use crate::canonical::records::LogicalEvent;
use crate::canonical::types::{AccessEpoch, BlobId, KeyId, ObjectId, TicketId};
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
    pub safety: TicketSafetyState,
    pub messages: Vec<ChatMessageView>,
    pub attachments: Vec<AttachmentView>,
    pub incorporated_packs: Vec<ObjectId>,
}

/// Trait to resolve an author role from the writer key id.
pub trait RoleResolver {
    fn resolve_role(&self, writer_key_id: &KeyId) -> AuthorRole;
}

/// Default role resolver mapping specific client and operator keys.
pub struct SimpleRoleResolver {
    pub client_keys: HashSet<KeyId>,
    pub operator_keys: HashSet<KeyId>,
}

impl SimpleRoleResolver {
    pub fn new() -> Self {
        Self {
            client_keys: HashSet::new(),
            operator_keys: HashSet::new(),
        }
    }

    pub fn with_client(mut self, key_id: KeyId) -> Self {
        self.client_keys.insert(key_id);
        self
    }

    pub fn with_operator(mut self, key_id: KeyId) -> Self {
        self.operator_keys.insert(key_id);
        self
    }
}

impl Default for SimpleRoleResolver {
    fn default() -> Self {
        Self::new()
    }
}

impl RoleResolver for SimpleRoleResolver {
    fn resolve_role(&self, writer_key_id: &KeyId) -> AuthorRole {
        if self.client_keys.contains(writer_key_id) {
            AuthorRole::Client
        } else if self.operator_keys.contains(writer_key_id) {
            AuthorRole::Operator
        } else {
            AuthorRole::Admin
        }
    }
}

/// Permissive resolver defaulting to Client for tests/unconfigured scenarios.
pub struct DefaultRoleResolver;

impl RoleResolver for DefaultRoleResolver {
    fn resolve_role(&self, _writer_key_id: &KeyId) -> AuthorRole {
        AuthorRole::Client
    }
}

/// Deterministically reduces all valid events for `ticket_id` from the `EventGraph`.
pub fn reduce_ticket(ticket_id: TicketId, graph: &EventGraph) -> Option<TicketView> {
    reduce_ticket_with_resolver(ticket_id, graph, &DefaultRoleResolver)
}

/// Deterministically reduces events using an explicit author role resolver.
pub fn reduce_ticket_with_resolver(
    ticket_id: TicketId,
    graph: &EventGraph,
    resolver: &impl RoleResolver,
) -> Option<TicketView> {
    let all_pack_ids = graph.get_ticket_packs(&ticket_id);
    if all_pack_ids.is_empty() {
        return None;
    }

    // Optional admin canonical head filter
    let head_set_filter: Option<HashSet<ObjectId>> = graph
        .get_canonical_heads(&ticket_id)
        .map(|h| h.canonical_heads.iter().copied().collect());

    let mut view: Option<TicketView> = None;

    for &pack_id in all_pack_ids {
        // Exclude tombstoned packs
        if graph.is_tombstoned(&pack_id) {
            continue;
        }

        // If CanonicalHeadSet is set, ensure pack is part of the designated head set
        if let Some(ref heads) = head_set_filter {
            if !heads.contains(&pack_id) {
                continue;
            }
        }

        let plaintext = match graph.get_plaintext(&pack_id) {
            Some(p) => p,
            None => continue,
        };

        // Determine author role from the writer key id
        let role = graph
            .get_object(&pack_id)
            .map(|obj| resolver.resolve_role(&obj.tbs.writer_key_id))
            .unwrap_or(AuthorRole::Client);

        for event in &plaintext.events {
            match event {
                LogicalEvent::TicketCreated {
                    title,
                    initial_epoch,
                    ..
                } => {
                    let mut epoch_bytes = [0u8; 16];
                    epoch_bytes[..8].copy_from_slice(&initial_epoch.to_le_bytes());
                    let access_epoch = AccessEpoch::from_bytes(epoch_bytes);

                    let safety = TicketSafetyState::new_client_open(ticket_id, access_epoch);
                    view = Some(TicketView {
                        ticket_id,
                        title: title.clone(),
                        safety,
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
                LogicalEvent::AccessEpochRevoked { .. }
                | LogicalEvent::AccessEpochGranted { .. }
                | LogicalEvent::TicketStateChanged { .. } => {
                    if let Some(v) = &mut view {
                        v.safety.apply_transition(role, event);
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
