use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DesktopStatusDto {
    pub product: String,
    pub version: String,
    pub agent_state: String,
    pub mode: String,
    pub peer_id: String,
    pub active_ticket_id: Option<String>,
    pub active_ticket_state: Option<String>,
    pub authorized_operator: Option<String>,
    pub is_operator_unlocked: bool,
}

impl Default for DesktopStatusDto {
    fn default() -> Self {
        Self {
            product: "FORTIQ".to_string(),
            version: env!("CARGO_PKG_VERSION").to_string(),
            agent_state: "offline".to_string(),
            mode: "node".to_string(),
            peer_id: String::new(),
            active_ticket_id: None,
            active_ticket_state: None,
            authorized_operator: None,
            is_operator_unlocked: false,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DesktopPeerDto {
    pub peer_id: String,
    pub hostname: String,
    pub os: String,
    pub transport: String,
    pub status: String,
    pub mode: Option<String>,
    pub authorized_operator: Option<String>,
    pub relay: bool,
    pub rendezvous: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketSummaryDto {
    pub id: String,
    pub title: String,
    pub priority: u8,
    pub state: String,
    pub created_at: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChatMessageDto {
    pub id: String,
    pub sender: String,
    pub body: String,
    pub created_at: u64,
    pub is_operator: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttachmentDto {
    pub id: String,
    pub filename: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketDetailDto {
    pub id: String,
    pub title: String,
    pub priority: u8,
    pub state: String,
    pub created_at: u64,
    pub access_epoch: Option<String>,
    pub messages: Vec<ChatMessageDto>,
    pub attachments: Vec<AttachmentDto>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OperatorSessionDto {
    pub operator_entity: String,
    pub capabilities: Vec<String>,
    pub issued_at: u64,
    pub expires_at: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct SelfSupportStatusDto {
    pub db_corrupt: bool,
    pub peers_stale: bool,
    pub active_repair_count: u32,
    pub last_action_message: Option<String>,
}
