use serde::{Deserialize, Serialize};
use std::fmt;
use std::str::FromStr;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum TicketState {
    Open,
    InProgress,
    Resolved,
    Closed,
}

impl TicketState {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Open => "OPEN",
            Self::InProgress => "IN_PROGRESS",
            Self::Resolved => "RESOLVED",
            Self::Closed => "CLOSED",
        }
    }

    pub fn parse_str(s: &str) -> Option<Self> {
        s.parse().ok()
    }

    pub fn permits_work(&self) -> bool {
        matches!(self, Self::Open | Self::InProgress)
    }

    pub fn can_transition_to(self, next: Self) -> bool {
        self == next
            || matches!(
                (self, next),
                (Self::Open, Self::InProgress | Self::Resolved | Self::Closed)
                    | (Self::InProgress, Self::Resolved | Self::Closed)
                    | (Self::Resolved, Self::InProgress | Self::Closed)
            )
    }
}

impl fmt::Display for TicketState {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.as_str())
    }
}

impl FromStr for TicketState {
    type Err = anyhow::Error;

    fn from_str(s: &str) -> std::result::Result<Self, Self::Err> {
        match s {
            "OPEN" => Ok(Self::Open),
            "IN_PROGRESS" => Ok(Self::InProgress),
            "RESOLVED" => Ok(Self::Resolved),
            "CLOSED" => Ok(Self::Closed),
            _ => anyhow::bail!("Statut de ticket inconnu: {s}"),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum TicketPriority {
    Normal,
    High,
    Urgent,
}

impl TicketPriority {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Normal => "NORMAL",
            Self::High => "HIGH",
            Self::Urgent => "URGENT",
        }
    }

    pub fn parse_str(s: &str) -> Self {
        s.parse().unwrap_or(Self::Normal)
    }
}

impl fmt::Display for TicketPriority {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.as_str())
    }
}

impl FromStr for TicketPriority {
    type Err = std::convert::Infallible;

    fn from_str(s: &str) -> std::result::Result<Self, Self::Err> {
        match s {
            "HIGH" => Ok(Self::High),
            "URGENT" => Ok(Self::Urgent),
            _ => Ok(Self::Normal),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketRecord {
    pub id: String,
    pub title: String,
    pub description: String,
    pub state: TicketState,
    pub priority: TicketPriority,
    pub client_peer_id: String,
    #[serde(default)]
    pub revision: u64,
    pub created_at: u64,
    pub updated_at: u64,
    pub closed_at: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChatMessage {
    pub id: String,
    pub ticket_id: String,
    pub sender_peer_id: String,
    pub body: String,
    pub created_at: u64,
    pub delivery_state: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttachmentRecord {
    pub id: String,
    pub ticket_id: String,
    pub sender_peer_id: String,
    pub filename: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub local_path: String,
    pub created_at: u64,
    pub state: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShellSessionRecord {
    pub id: String,
    pub ticket_id: String,
    pub operator_peer_id: String,
    pub started_at: u64,
    pub ended_at: Option<u64>,
    pub transport: String,
    pub result: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketEventRecord {
    pub id: String,
    pub ticket_id: String,
    pub kind: String,
    pub actor_peer_id: String,
    pub timestamp: u64,
    pub metadata: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketDetail {
    pub ticket: TicketRecord,
    pub messages: Vec<ChatMessage>,
    pub attachments: Vec<AttachmentRecord>,
    pub shell_sessions: Vec<ShellSessionRecord>,
    pub events: Vec<TicketEventRecord>,
}
