//! Self-Support Session and Execution Engine on "This Device".
//!
//! Enforces:
//! - Offline operation: self-support tickets and local diagnostics execute without external network.
//! - Invariant #6: Client owns shell safety gate. Even on the local machine,
//!   AccessEpoch validation and immediate client revocation are strictly enforced.
//! - Anti-resurrection: revoked epochs cannot be resurrected.

use std::collections::HashMap;
use thiserror::Error;

use crate::canonical::self_support::this_device::{
    LocalDiagnostics, StorageDiagnostics, ThisDevice,
};
use crate::canonical::shell::challenge::{ShellAuthError, ShellAuthResponse, ShellChallenge};
use crate::canonical::shell::session::{SessionRevocationGuard, SessionSafetyGate};
use crate::canonical::signing::Verifier;
use crate::canonical::types::{EntityId, TicketId};
use crate::TicketState;

#[derive(Debug, Error)]
pub enum SelfSupportError {
    #[error("Ticket not found: {0:?}")]
    TicketNotFound(TicketId),
    #[error("Ticket is closed")]
    TicketClosed,
    #[error("Shell auth error: {0}")]
    Auth(#[from] ShellAuthError),
}

/// Metadata and state of a self-support ticket created on "This Device".
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelfSupportTicket {
    pub ticket_id: TicketId,
    pub title: String,
    pub description: String,
    pub creator_id: EntityId,
    pub created_at: u64,
    pub is_closed: bool,
    pub is_self_support: bool,
}

/// Engine managing sovereign loopback self-support operations on "This Device".
pub struct SelfSupportEngine {
    this_device: ThisDevice,
    active_guards: HashMap<TicketId, SessionRevocationGuard>,
    tickets: HashMap<TicketId, SelfSupportTicket>,
}

impl SelfSupportEngine {
    /// Initializes a new SelfSupportEngine for the given local device.
    pub fn new(this_device: ThisDevice) -> Self {
        Self {
            this_device,
            active_guards: HashMap::new(),
            tickets: HashMap::new(),
        }
    }

    /// Returns a reference to the local device representation.
    pub fn this_device(&self) -> &ThisDevice {
        &self.this_device
    }

    /// Gathers local system diagnostics completely offline.
    pub fn collect_diagnostics(&self, storage: Option<StorageDiagnostics>) -> LocalDiagnostics {
        self.this_device.collect_diagnostics(storage)
    }

    /// Creates a self-support ticket on the local machine.
    pub fn create_self_support_ticket(
        &mut self,
        title: String,
        description: String,
        creator_id: EntityId,
    ) -> Result<SelfSupportTicket, SelfSupportError> {
        let ticket_id = TicketId::from_bytes(uuid::Uuid::new_v4().into_bytes());
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);

        let ticket = SelfSupportTicket {
            ticket_id,
            title,
            description,
            creator_id,
            created_at: now,
            is_closed: false,
            is_self_support: true,
        };

        self.tickets.insert(ticket_id, ticket.clone());
        Ok(ticket)
    }

    /// Retrieves a self-support ticket by TicketId.
    pub fn get_ticket(&self, ticket_id: &TicketId) -> Option<&SelfSupportTicket> {
        self.tickets.get(ticket_id)
    }

    /// Creates a shell challenge for authenticating a loopback shell session.
    pub fn create_shell_challenge(
        &self,
        ticket_id: &TicketId,
    ) -> Result<ShellChallenge, SelfSupportError> {
        let ticket = self
            .tickets
            .get(ticket_id)
            .ok_or(SelfSupportError::TicketNotFound(*ticket_id))?;

        if ticket.is_closed {
            return Err(SelfSupportError::TicketClosed);
        }

        Ok(ShellChallenge::new(*ticket_id))
    }

    /// Verifies the operator's response to the challenge and establishes an authenticated shell session.
    pub fn authorize_and_open_shell(
        &mut self,
        challenge: &ShellChallenge,
        auth_response: &ShellAuthResponse,
        verifier: &impl Verifier,
    ) -> Result<SessionRevocationGuard, SelfSupportError> {
        let ticket = self
            .tickets
            .get(&challenge.ticket_id)
            .ok_or(SelfSupportError::TicketNotFound(challenge.ticket_id))?;

        if ticket.is_closed {
            return Err(SelfSupportError::TicketClosed);
        }

        // Cryptographically verify the challenge signature.
        auth_response.verify(verifier, challenge)?;

        let guard = SessionSafetyGate::authorize_session(challenge.ticket_id, TicketState::Open)
            .map_err(SelfSupportError::Auth)?;

        self.active_guards
            .insert(challenge.ticket_id, guard.clone());
        Ok(guard)
    }

    /// Immediately revokes a shell session on "This Device".
    pub fn close_shell(
        &mut self,
        ticket_id: &TicketId,
        reason: &str,
    ) -> Result<(), SelfSupportError> {
        if let Some(guard) = self.active_guards.remove(ticket_id) {
            guard.revoke(reason);
        }
        Ok(())
    }

    /// Closes a self-support ticket and revokes any active shell sessions.
    pub fn close_ticket(
        &mut self,
        ticket_id: &TicketId,
        reason: &str,
    ) -> Result<(), SelfSupportError> {
        let ticket = self
            .tickets
            .get_mut(ticket_id)
            .ok_or(SelfSupportError::TicketNotFound(*ticket_id))?;

        ticket.is_closed = true;
        self.close_shell(ticket_id, reason)?;
        Ok(())
    }
}
