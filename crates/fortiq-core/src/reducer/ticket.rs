use anyhow::{bail, Result};
use std::collections::HashMap;

use crate::event::{Event, EventGraph, EventPayload};
use crate::ticket::{
    AttachmentRecord, ChatMessage, ShellSessionRecord, TicketDetail, TicketEvent,
    TicketEventRecord, TicketRecord, TicketState,
};

/// An in-memory aggregate for a single ticket, containing all its reduced state and related entities.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TicketAggregate {
    pub record: TicketRecord,
    pub messages: Vec<ChatMessage>,
    pub attachments: Vec<AttachmentRecord>,
    pub shell_sessions: Vec<ShellSessionRecord>,
    pub events: Vec<TicketEventRecord>,
}

impl TicketAggregate {
    pub fn to_detail(&self) -> TicketDetail {
        TicketDetail {
            ticket: self.record.clone(),
            messages: self.messages.clone(),
            attachments: self.attachments.clone(),
            shell_sessions: self.shell_sessions.clone(),
            events: self.events.clone(),
        }
    }
}

/// The pure in-memory state store containing all reduced tickets.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TicketStateStore {
    tickets: HashMap<String, TicketAggregate>,
}

impl TicketStateStore {
    pub fn new() -> Self {
        Self {
            tickets: HashMap::new(),
        }
    }

    pub fn tickets(&self) -> &HashMap<String, TicketAggregate> {
        &self.tickets
    }

    pub fn tickets_mut(&mut self) -> &mut HashMap<String, TicketAggregate> {
        &mut self.tickets
    }

    pub fn list_tickets(&self, state_filter: Option<TicketState>) -> Vec<TicketRecord> {
        let mut list: Vec<TicketRecord> = self
            .tickets
            .values()
            .map(|a| a.record.clone())
            .filter(|r| state_filter.is_none_or(|f| r.state == f))
            .collect();
        // Sort descending by updated_at, then ID
        list.sort_by(|a, b| {
            b.updated_at
                .cmp(&a.updated_at)
                .then_with(|| a.id.cmp(&b.id))
        });
        list
    }

    pub fn get_ticket(&self, ticket_id: &str) -> Option<TicketRecord> {
        self.tickets.get(ticket_id).map(|a| a.record.clone())
    }

    pub fn get_ticket_detail(&self, ticket_id: &str) -> Option<TicketDetail> {
        self.tickets.get(ticket_id).map(|a| a.to_detail())
    }

    pub fn list_messages(&self, ticket_id: &str) -> Vec<ChatMessage> {
        self.tickets
            .get(ticket_id)
            .map(|a| a.messages.clone())
            .unwrap_or_default()
    }

    pub fn list_attachments(&self, ticket_id: &str) -> Vec<AttachmentRecord> {
        self.tickets
            .get(ticket_id)
            .map(|a| a.attachments.clone())
            .unwrap_or_default()
    }

    pub fn list_shell_sessions(&self, ticket_id: &str) -> Vec<ShellSessionRecord> {
        self.tickets
            .get(ticket_id)
            .map(|a| a.shell_sessions.clone())
            .unwrap_or_default()
    }

    pub fn get_active_ticket(&self) -> Option<TicketRecord> {
        let all = self.list_tickets(None);
        all.into_iter().find(|t| t.state.permits_work())
    }
}

/// Pure deterministic Reducer from causal EventGraph DAG to TicketStateStore.
pub struct TicketReducer;

impl TicketReducer {
    /// Pure function that folds the entire EventGraph in deterministic topological order into in-memory state.
    pub fn reduce(graph: &EventGraph) -> TicketStateStore {
        let mut store = TicketStateStore::new();
        for event in graph.topological_sort() {
            let _ = Self::apply_event(&mut store, event);
        }
        store
    }

    /// Incremental event application to an existing in-memory store.
    pub fn apply_event(store: &mut TicketStateStore, event: &Event) -> Result<()> {
        let ticket_event = match &event.payload {
            EventPayload::Ticket(te) => te,
            _ => return Ok(()),
        };

        let actor_peer_id = event.author.derive_id().to_hex();

        match ticket_event {
            TicketEvent::Created {
                ticket_id,
                title,
                description,
                priority,
                client_peer_id,
            } => {
                if store.tickets.contains_key(ticket_id) {
                    bail!("ticket already exists: {ticket_id}");
                }

                let record = TicketRecord {
                    id: ticket_id.clone(),
                    title: title.clone(),
                    description: description.clone(),
                    state: TicketState::Open,
                    priority: *priority,
                    client_peer_id: client_peer_id.clone(),
                    revision: 1,
                    created_at: event.timestamp,
                    updated_at: event.timestamp,
                    closed_at: None,
                };

                let audit_event = TicketEventRecord {
                    id: event.id.to_hex(),
                    ticket_id: ticket_id.clone(),
                    kind: "CREATED".to_string(),
                    actor_peer_id,
                    timestamp: event.timestamp,
                    metadata: None,
                };

                store.tickets.insert(
                    ticket_id.clone(),
                    TicketAggregate {
                        record,
                        messages: Vec::new(),
                        attachments: Vec::new(),
                        shell_sessions: Vec::new(),
                        events: vec![audit_event],
                    },
                );
            }

            TicketEvent::StatusChanged {
                ticket_id,
                new_state,
                actor_peer_id: _,
                reason,
            } => {
                let agg = match store.tickets.get_mut(ticket_id) {
                    Some(a) => a,
                    None => bail!("ticket not found: {ticket_id}"),
                };

                if !agg.record.state.can_transition_to(*new_state) {
                    bail!(
                        "invalid transition for ticket {} from {:?} to {:?}",
                        ticket_id,
                        agg.record.state,
                        new_state
                    );
                }

                agg.record.state = *new_state;
                agg.record.revision += 1;
                agg.record.updated_at = event.timestamp;
                if *new_state == TicketState::Closed {
                    agg.record.closed_at = Some(event.timestamp);
                }

                agg.events.push(TicketEventRecord {
                    id: event.id.to_hex(),
                    ticket_id: ticket_id.clone(),
                    kind: format!("STATUS_CHANGED_TO_{}", new_state.as_str()),
                    actor_peer_id,
                    timestamp: event.timestamp,
                    metadata: reason.clone(),
                });
            }

            TicketEvent::ChatMessageAdded {
                ticket_id,
                message_id,
                sender_peer_id,
                body,
                timestamp,
            } => {
                let agg = match store.tickets.get_mut(ticket_id) {
                    Some(a) => a,
                    None => bail!("ticket not found: {ticket_id}"),
                };

                if !agg.messages.iter().any(|m| m.id == *message_id) {
                    agg.messages.push(ChatMessage {
                        id: message_id.clone(),
                        ticket_id: ticket_id.clone(),
                        sender_peer_id: sender_peer_id.clone(),
                        body: body.clone(),
                        created_at: *timestamp,
                        delivery_state: "DELIVERED".to_string(),
                    });
                    agg.record.updated_at = event.timestamp;
                    agg.record.revision += 1;
                }
            }

            TicketEvent::AttachmentAdded {
                ticket_id,
                attachment_id,
                sender_peer_id,
                filename,
                size_bytes,
                sha256,
                local_path,
                timestamp,
            } => {
                let agg = match store.tickets.get_mut(ticket_id) {
                    Some(a) => a,
                    None => bail!("ticket not found: {ticket_id}"),
                };

                if !agg.attachments.iter().any(|a| a.id == *attachment_id) {
                    agg.attachments.push(AttachmentRecord {
                        id: attachment_id.clone(),
                        ticket_id: ticket_id.clone(),
                        sender_peer_id: sender_peer_id.clone(),
                        filename: filename.clone(),
                        size_bytes: *size_bytes,
                        sha256: sha256.clone(),
                        local_path: local_path.clone(),
                        created_at: *timestamp,
                        state: "AVAILABLE".to_string(),
                    });
                    agg.record.updated_at = event.timestamp;
                    agg.record.revision += 1;
                }
            }

            TicketEvent::ShellSessionStarted {
                ticket_id,
                session_id,
                operator_peer_id,
                transport,
                started_at,
            } => {
                let agg = match store.tickets.get_mut(ticket_id) {
                    Some(a) => a,
                    None => bail!("ticket not found: {ticket_id}"),
                };

                if !agg.shell_sessions.iter().any(|s| s.id == *session_id) {
                    agg.shell_sessions.push(ShellSessionRecord {
                        id: session_id.clone(),
                        ticket_id: ticket_id.clone(),
                        operator_peer_id: operator_peer_id.clone(),
                        started_at: *started_at,
                        ended_at: None,
                        transport: transport.clone(),
                        result: None,
                    });
                    agg.record.updated_at = event.timestamp;
                    agg.record.revision += 1;
                }
            }

            TicketEvent::ShellSessionEnded {
                ticket_id,
                session_id,
                ended_at,
                result,
            } => {
                let agg = match store.tickets.get_mut(ticket_id) {
                    Some(a) => a,
                    None => bail!("ticket not found: {ticket_id}"),
                };

                if let Some(session) = agg.shell_sessions.iter_mut().find(|s| s.id == *session_id) {
                    session.ended_at = Some(*ended_at);
                    session.result = result.clone();
                    agg.record.updated_at = event.timestamp;
                    agg.record.revision += 1;
                }
            }

            TicketEvent::CustomAudit {
                ticket_id,
                event_id,
                kind,
                actor_peer_id,
                metadata,
                timestamp,
            } => {
                let agg = match store.tickets.get_mut(ticket_id) {
                    Some(a) => a,
                    None => bail!("ticket not found: {ticket_id}"),
                };

                if !agg.events.iter().any(|e| e.id == *event_id) {
                    agg.events.push(crate::ticket::TicketEventRecord {
                        id: event_id.clone(),
                        ticket_id: ticket_id.clone(),
                        kind: kind.clone(),
                        actor_peer_id: actor_peer_id.clone(),
                        timestamp: *timestamp,
                        metadata: metadata.clone(),
                    });
                    agg.record.updated_at = event.timestamp;
                    agg.record.revision += 1;
                }
            }
        }

        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::object::Ed25519Signer;
    use crate::ticket::TicketPriority;

    #[test]
    fn test_reduction_lifecycle() {
        let signer = Ed25519Signer::generate();
        let mut graph = EventGraph::new();

        // 1. Create ticket
        let e1_payload = EventPayload::Ticket(TicketEvent::Created {
            ticket_id: "T-100".into(),
            title: "Kernel panic".into(),
            description: "Null pointer".into(),
            priority: TicketPriority::Urgent,
            client_peer_id: "client-1".into(),
        });
        let (e1, _) = Event::new(vec![], 1_000, 1, e1_payload, &signer).unwrap();
        graph.insert(e1.clone()).unwrap();

        // 2. Chat message
        let e2_payload = EventPayload::Ticket(TicketEvent::ChatMessageAdded {
            ticket_id: "T-100".into(),
            message_id: "M-1".into(),
            sender_peer_id: "client-1".into(),
            body: "Logs attached".into(),
            timestamp: 1_010,
        });
        let (e2, _) = Event::new(vec![e1.id], 1_010, 2, e2_payload, &signer).unwrap();
        graph.insert(e2.clone()).unwrap();

        // 3. Status change to IN_PROGRESS
        let e3_payload = EventPayload::Ticket(TicketEvent::StatusChanged {
            ticket_id: "T-100".into(),
            new_state: TicketState::InProgress,
            actor_peer_id: "operator-1".into(),
            reason: Some("Investigating".into()),
        });
        let (e3, _) = Event::new(vec![e2.id], 1_020, 3, e3_payload, &signer).unwrap();
        graph.insert(e3.clone()).unwrap();

        // Reduce!
        let store = TicketReducer::reduce(&graph);
        let ticket = store.get_ticket("T-100").expect("ticket should exist");
        assert_eq!(ticket.state, TicketState::InProgress);
        assert_eq!(ticket.revision, 3);

        let messages = store.list_messages("T-100");
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].body, "Logs attached");

        let detail = store.get_ticket_detail("T-100").unwrap();
        assert_eq!(detail.events.len(), 2); // Created + StatusChanged
    }
}
