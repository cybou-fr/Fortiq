use serde::{Deserialize, Serialize};

use super::state::{TicketPriority, TicketState};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", content = "data")]
pub enum TicketEvent {
    Created {
        ticket_id: String,
        title: String,
        description: String,
        priority: TicketPriority,
        client_peer_id: String,
    },
    StatusChanged {
        ticket_id: String,
        new_state: TicketState,
        actor_peer_id: String,
        #[serde(default)]
        reason: Option<String>,
    },
    ChatMessageAdded {
        ticket_id: String,
        message_id: String,
        sender_peer_id: String,
        body: String,
        timestamp: u64,
    },
    AttachmentAdded {
        ticket_id: String,
        attachment_id: String,
        sender_peer_id: String,
        filename: String,
        size_bytes: u64,
        sha256: String,
        local_path: String,
        timestamp: u64,
    },
    ShellSessionStarted {
        ticket_id: String,
        session_id: String,
        operator_peer_id: String,
        transport: String,
        started_at: u64,
    },
    ShellSessionEnded {
        ticket_id: String,
        session_id: String,
        ended_at: u64,
        #[serde(default)]
        result: Option<String>,
    },
    CustomAudit {
        ticket_id: String,
        event_id: String,
        kind: String,
        actor_peer_id: String,
        #[serde(default)]
        metadata: Option<String>,
        timestamp: u64,
    },
}

impl TicketEvent {
    pub fn ticket_id(&self) -> &str {
        match self {
            Self::Created { ticket_id, .. }
            | Self::StatusChanged { ticket_id, .. }
            | Self::ChatMessageAdded { ticket_id, .. }
            | Self::AttachmentAdded { ticket_id, .. }
            | Self::ShellSessionStarted { ticket_id, .. }
            | Self::ShellSessionEnded { ticket_id, .. }
            | Self::CustomAudit { ticket_id, .. } => ticket_id,
        }
    }
}
