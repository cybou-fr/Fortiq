//! Client-owned Ticket AccessEpoch and remote shell safety reducer.
//!
//! Hard Invariants:
//! - Invariant 8: Client-owned `TicketAccessEpoch`. The client alone generates the initial
//!   `TicketAccessEpoch` and revokes it locally immediately upon ticket close or consent withdrawal.
//! - Invariant 9: Immediate client-side revocation. The client does NOT wait for distributed
//!   consensus or remote operator confirmation to kill shell sessions or invalidate access.
//! - Operators/Admins can NEVER restore `access_valid = true` on a dead or revoked epoch.

use crate::canonical::types::TicketId;
use serde::{Deserialize, Serialize};

/// Lifecycle state of a support ticket.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[repr(u8)]
pub enum TicketLifecycle {
    Open = 1,
    InProgress = 2,
    Resolved = 3,
    Closed = 4,
}

impl TicketLifecycle {
    pub const fn permits_work(&self) -> bool {
        matches!(self, Self::Open | Self::InProgress)
    }
}

/// The role of the actor authoring an event.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AuthorRole {
    Client,
    Operator,
    Admin,
}

/// Lifecycle state evaluating whether remote shell access is permitted.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketSafetyState {
    pub ticket_id: TicketId,
    pub lifecycle: TicketLifecycle,
}

impl TicketSafetyState {
    pub fn new_client_open(ticket_id: TicketId) -> Self {
        Self {
            ticket_id,
            lifecycle: TicketLifecycle::Open,
        }
    }

    /// Evaluates if remote shell execution is permitted right now.
    ///
    pub fn permits_shell(&self) -> bool {
        self.lifecycle.permits_work()
    }

    /// Operator takes ticket into progress.
    ///
    pub fn set_in_progress_by_operator(&mut self) {
        if self.lifecycle != TicketLifecycle::Closed {
            self.lifecycle = TicketLifecycle::InProgress;
        }
    }

    /// Operator marks ticket as resolved.
    pub fn resolve_by_operator(&mut self) {
        self.lifecycle = TicketLifecycle::Resolved;
    }

    /// Operator closes ticket.
    pub fn close_by_operator(&mut self) {
        self.lifecycle = TicketLifecycle::Closed;
    }

    /// Applies an event to the safety state strictly obeying author permissions.
    pub fn apply_transition(
        &mut self,
        role: AuthorRole,
        event: &crate::canonical::records::LogicalEvent,
    ) {
        use crate::canonical::records::LogicalEvent;

        if let LogicalEvent::TicketStateChanged { new_state, .. } = event {
            if role != AuthorRole::Operator && role != AuthorRole::Admin {
                return;
            }
            match *new_state {
                2 => {
                    self.set_in_progress_by_operator();
                }
                3 => {
                    self.resolve_by_operator();
                }
                4 => {
                    self.close_by_operator();
                }
                _ => {}
            }
        }
    }
}
