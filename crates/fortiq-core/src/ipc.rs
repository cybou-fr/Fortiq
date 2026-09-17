use serde::{Deserialize, Serialize};

use crate::{NodeMode, Ticket};

pub const DEFAULT_WINDOWS_PIPE_NAME: &str = r"\\.\pipe\fortiq-ipc";
pub const DEFAULT_UNIX_SOCKET_PATH: &str = "/run/fortiq.sock";

pub const DEFAULT_WINDOWS_TERMINAL_PIPE_NAME: &str = r"\\.\pipe\fortiq-terminal";
pub const DEFAULT_UNIX_TERMINAL_SOCKET_PATH: &str = "/run/fortiq-terminal.sock";

pub fn windows_pipe_name() -> String {
    std::env::var("FORTIQ_PIPE").unwrap_or_else(|_| DEFAULT_WINDOWS_PIPE_NAME.to_owned())
}

pub fn windows_terminal_pipe_name() -> String {
    std::env::var("FORTIQ_TERMINAL_PIPE")
        .unwrap_or_else(|_| DEFAULT_WINDOWS_TERMINAL_PIPE_NAME.to_owned())
}

pub fn unix_socket_path() -> String {
    std::env::var("FORTIQ_SOCK").unwrap_or_else(|_| DEFAULT_UNIX_SOCKET_PATH.to_owned())
}

pub fn unix_terminal_socket_path() -> String {
    std::env::var("FORTIQ_TERMINAL_SOCK")
        .unwrap_or_else(|_| DEFAULT_UNIX_TERMINAL_SOCKET_PATH.to_owned())
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct TerminalSessionInit {
    pub peer: String,
    #[serde(default)]
    pub ticket_id: Option<String>,
    #[serde(default = "default_terminal_cols")]
    pub cols: u16,
    #[serde(default = "default_terminal_rows")]
    pub rows: u16,
    #[serde(default)]
    pub dial: Option<String>,
}

fn default_terminal_cols() -> u16 {
    80
}

fn default_terminal_rows() -> u16 {
    24
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "type", content = "payload")]
pub enum IpcRequest {
    GetStatus,
    OpenTicket,
    ListPeers,

    // Ticket Core v2 additions:
    ListTickets {
        #[serde(default)]
        state_filter: Option<crate::TicketState>,
    },
    GetTicket {
        ticket_id: String,
    },
    CreateTicket {
        title: String,
        description: String,
        priority: crate::TicketPriority,
    },
    UpdateTicketStatus {
        ticket_id: String,
        state: crate::TicketState,
    },
    SetRemoteAccess {
        ticket_id: String,
        enabled: bool,
    },
    SendChatMessage {
        ticket_id: String,
        body: String,
    },
    ListMessages {
        ticket_id: String,
    },
    SendFile {
        ticket_id: String,
        file_path: String,
    },
    ListAttachments {
        ticket_id: String,
    },
    ListShellSessions {
        ticket_id: String,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "type", content = "payload")]
pub enum IpcResponse {
    Status(DaemonStatus),
    TicketOpened(Ticket),
    Peers(Vec<PeerSummary>),
    Tickets(Vec<crate::TicketRecord>),
    TicketDetail(Option<crate::TicketDetail>),
    TicketCreated(crate::TicketRecord),
    TicketUpdated(Option<crate::TicketRecord>),
    Messages(Vec<crate::ChatMessage>),
    MessageSent(crate::ChatMessage),
    Attachments(Vec<crate::AttachmentRecord>),
    FileSent(crate::AttachmentRecord),
    ShellSessions(Vec<crate::ShellSessionRecord>),
    Success,
    Error(String),
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DaemonStatus {
    pub product: String,
    pub version: String,
    pub mode: NodeMode,
    pub peer_id: String,
    pub agent_state: String,
    pub active_ticket: Option<Ticket>,
    pub authorized_operator: Option<String>,
    pub listen_addresses: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct PeerSummary {
    pub peer_id: String,
    pub hostname: String,
    pub os: String,
    pub transport: String,
    pub status: String,
    pub mode: Option<NodeMode>,
    pub authorized_operator: Option<String>,
    pub relay: bool,
    pub rendezvous: bool,
}
