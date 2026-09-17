use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

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

pub fn upload_spool_dir() -> PathBuf {
    if let Some(path) = std::env::var_os("FORTIQ_UPLOAD_SPOOL") {
        return PathBuf::from(path);
    }
    #[cfg(windows)]
    if let Some(public) = std::env::var_os("PUBLIC") {
        return PathBuf::from(public)
            .join("Documents")
            .join("FortiqUploadSpool");
    }
    std::env::temp_dir().join("fortiq-upload-spool")
}

pub fn new_upload_staging_path(source: &Path) -> std::io::Result<PathBuf> {
    let root = upload_spool_dir();
    std::fs::create_dir_all(&root)?;
    let user = std::env::var("USERNAME")
        .or_else(|_| std::env::var("USER"))
        .unwrap_or_else(|_| "unknown-user".to_string());
    use sha2::{Digest, Sha256};
    let user_key = format!("{:x}", Sha256::digest(user.as_bytes()));
    let user_root = root.join(user_key);
    std::fs::create_dir_all(&user_root)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&user_root, std::fs::Permissions::from_mode(0o700))?;
    }
    let name = source
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("attachment.bin")
        .replace(['/', '\\', ':', '*', '?', '"', '<', '>', '|'], "_");
    Ok(user_root.join(format!("{}_{}", uuid::Uuid::new_v4().simple(), name)))
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
    SendChatMessage {
        ticket_id: String,
        body: String,
    },
    ListMessages {
        ticket_id: String,
    },
    SendFile {
        ticket_id: String,
        staged_path: String,
    },
    ListAttachments {
        ticket_id: String,
    },
    ListShellSessions {
        ticket_id: String,
    },

    // Canonical Operator Authority:
    UnlockOperator {
        mnemonic: String,
    },
    LockOperator,
    GetOperatorStatus,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "type", content = "payload")]
pub enum IpcResponse {
    Status(DaemonStatus),
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
    OperatorStatus(OperatorSessionStatus),
    Success,
    Error(String),
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
pub struct OperatorSessionStatus {
    pub is_unlocked: bool,
    #[serde(default)]
    pub owner_id: Option<String>,
    #[serde(default)]
    pub expires_at: Option<u64>,
    #[serde(default)]
    pub capabilities: Vec<String>,
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
    #[serde(default)]
    pub is_operator_unlocked: bool,
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
