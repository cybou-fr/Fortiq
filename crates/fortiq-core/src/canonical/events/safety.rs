//! Client-owned Ticket AccessEpoch and remote shell safety reducer.
//!
//! Hard Invariants:
//! - Invariant 8: Client-owned `TicketAccessEpoch`. The client alone generates the initial
//!   `TicketAccessEpoch` and revokes it locally immediately upon ticket close or consent withdrawal.
//! - Invariant 9: Immediate client-side revocation. The client does NOT wait for distributed
//!   consensus or remote operator confirmation to kill shell sessions or invalidate access.
//! - Operators/Admins can NEVER restore `access_valid = true` on a dead or revoked epoch.

use crate::canonical::types::{AccessEpoch, TicketId};
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

/// Safety state evaluating whether remote shell access is permitted.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketSafetyState {
    pub ticket_id: TicketId,
    pub access_epoch: AccessEpoch,
    pub access_valid: bool,
    pub lifecycle: TicketLifecycle,
}

impl TicketSafetyState {
    /// Creates an initial valid safety state authored by the Client.
    pub fn new_client_open(ticket_id: TicketId, initial_epoch: AccessEpoch) -> Self {
        Self {
            ticket_id,
            access_epoch: initial_epoch,
            access_valid: true,
            lifecycle: TicketLifecycle::Open,
        }
    }

    /// Evaluates if remote shell execution is permitted right now.
    ///
    /// Shell access is granted if and only if:
    /// 1. The client-owned `access_epoch` is valid (`access_valid == true`);
    /// 2. The ticket is in `Open` or `InProgress` state.
    pub fn permits_shell(&self) -> bool {
        self.access_valid && self.lifecycle.permits_work()
    }

    /// Client immediately revokes access locally without waiting for remote confirmation.
    pub fn revoke_by_client(&mut self) {
        self.access_valid = false;
    }

    /// Client closes ticket.
    pub fn close_by_client(&mut self) {
        self.access_valid = false;
        self.lifecycle = TicketLifecycle::Closed;
    }

    /// Client re-opens ticket with a brand new cryptographically generated AccessEpoch.
    pub fn reopen_by_client(&mut self, new_epoch: AccessEpoch) {
        self.access_epoch = new_epoch;
        self.access_valid = true;
        self.lifecycle = TicketLifecycle::Open;
    }

    /// Operator takes ticket into progress.
    ///
    /// If the client has already revoked access, the operator CANNOT restore access.
    pub fn set_in_progress_by_operator(&mut self) {
        if self.lifecycle != TicketLifecycle::Closed {
            self.lifecycle = TicketLifecycle::InProgress;
        }
    }

    /// Operator marks ticket as resolved.
    pub fn resolve_by_operator(&mut self) {
        self.lifecycle = TicketLifecycle::Resolved;
        self.access_valid = false;
    }

    /// Operator closes ticket.
    pub fn close_by_operator(&mut self) {
        self.lifecycle = TicketLifecycle::Closed;
        self.access_valid = false;
    }

    /// Applies an event to the safety state strictly obeying author permissions.
    pub fn apply_transition(
        &mut self,
        role: AuthorRole,
        event: &crate::canonical::records::LogicalEvent,
    ) {
        use crate::canonical::records::LogicalEvent;

        match event {
            LogicalEvent::AccessEpochRevoked { access_epoch, .. } => {
                // Anyone or client can trigger safety revocation; client-side is immediate
                if *access_epoch == *self.access_epoch.as_bytes() {
                    self.access_valid = false;
                }
            }
            LogicalEvent::AccessEpochGranted { access_epoch, .. } => {
                // HARD INVARIANT: ONLY the Client can grant or reopen an AccessEpoch!
                // An Operator/Admin can NEVER grant or resurrect access.
                if role == AuthorRole::Client {
                    self.access_epoch = AccessEpoch::from_bytes(*access_epoch);
                    self.access_valid = true;
                    if self.lifecycle == TicketLifecycle::Closed {
                        self.lifecycle = TicketLifecycle::Open;
                    }
                }
            }
            LogicalEvent::TicketStateChanged { new_state, .. } => {
                match *new_state {
                    2 => {
                        // InProgress
                        if role == AuthorRole::Operator || role == AuthorRole::Admin {
                            self.set_in_progress_by_operator();
                        }
                    }
                    3 => {
                        // Resolved
                        self.resolve_by_operator();
                    }
                    4 => {
                        // Closed
                        if role == AuthorRole::Client {
                            self.close_by_client();
                        } else {
                            self.close_by_operator();
                        }
                    }
                    _ => {}
                }
            }
            _ => {}
        }
    }
}
