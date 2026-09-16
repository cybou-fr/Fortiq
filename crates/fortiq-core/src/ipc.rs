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
    pub mode: Option<NodeMode>,
    pub authorized_operator: Option<String>,
    pub relay: bool,
    pub rendezvous: bool,
}
