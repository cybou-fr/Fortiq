use serde::{Deserialize, Serialize};

use crate::{NodeMode, Ticket};

pub const DEFAULT_WINDOWS_PIPE_NAME: &str = r"\\.\pipe\fortiq-ipc";
pub const DEFAULT_UNIX_SOCKET_PATH: &str = "/run/fortiq.sock";

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "type", content = "payload")]
pub enum IpcRequest {
    GetStatus,
    OpenTicket,
    CloseTicket {
        peer: String,
        #[serde(default)]
        dial: Option<String>,
    },
    ListPeers,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "type", content = "payload")]
pub enum IpcResponse {
    Status(DaemonStatus),
    TicketOpened(Ticket),
    TicketClosed,
    Peers(Vec<PeerSummary>),
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
}
