use std::path::PathBuf;

/// Commands sent from the Slint GUI / presentation thread to the BackendActor.
#[derive(Debug, Clone)]
pub enum DesktopCommand {
    /// Incremental poll of daemon status, peers, and tickets.
    Refresh,
    /// Select and load full details of a specific ticket.
    SelectTicket(String),
    /// Create a new local ticket.
    CreateTicket { title: String, priority: u8 },
    /// Post a chat message to the active ticket.
    SendMessage { ticket_id: String, body: String },
    /// Attach a staged local file to the active ticket.
    SendFile { ticket_id: String, path: PathBuf },
    /// Initiate a remote shell session for the given ticket.
    StartShell {
        ticket_id: String,
        cols: u16,
        rows: u16,
    },
    /// Send raw keyboard / input bytes to the running shell session.
    ShellInput(Vec<u8>),
    /// Inform the shell pseudo-terminal of window geometry resize.
    ResizeShell { cols: u16, rows: u16 },
    /// Terminate the active shell session gracefully.
    CloseShell,
    /// Unlock the portable operator workspace using a 24-word BIP-39 mnemonic.
    UnlockOperator(String),
    /// Lock and wipe the portable operator workspace and session certificate.
    LockOperator,
    /// Trigger an automated self-support diagnostic or repair action.
    TriggerSelfSupportAction(String),
    /// Refresh self-support diagnostic state.
    RefreshSelfSupport,
}
