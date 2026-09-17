use crate::models::{
    AttachmentDto, ChatMessageDto, DesktopPeerDto, DesktopStatusDto, OperatorSessionDto,
    SelfSupportStatusDto, TicketDetailDto, TicketSummaryDto,
};

/// Events emitted from the BackendActor to the Slint GUI thread.
#[derive(Debug, Clone)]
pub enum DesktopEvent {
    /// Daemon connectivity or agent lifecycle status changed.
    StatusChanged(DesktopStatusDto),
    /// Peer discovery list updated.
    PeersChanged(Vec<DesktopPeerDto>),
    /// Tickets list refreshed.
    TicketsChanged(Vec<TicketSummaryDto>),
    /// Full detail loaded for the selected ticket.
    TicketLoaded(Option<TicketDetailDto>),
    /// A new message was added to a ticket.
    MessageAdded {
        ticket_id: String,
        message: ChatMessageDto,
    },
    /// A file was attached to a ticket.
    FileAdded {
        ticket_id: String,
        file: AttachmentDto,
    },
    /// Shell session connected and pseudo-terminal opened.
    ShellOpened {
        ticket_id: String,
    },
    /// Raw terminal output bytes received from remote PTY.
    ShellOutput(Vec<u8>),
    /// Shell session ended.
    ShellClosed,
    /// Shell session attempt denied by safety gate or epoch.
    ShellDenied(String),
    /// Operator unlocked with valid session certificate.
    OperatorUnlocked(OperatorSessionDto),
    /// Operator session locked and secrets wiped.
    OperatorLocked,
    /// Diagnostic self-support state updated.
    SelfSupportChanged(SelfSupportStatusDto),
    /// User notification or feedback toast.
    Notification {
        level: String,
        message: String,
    },
    /// Error encountered during operation.
    Error(String),
}
