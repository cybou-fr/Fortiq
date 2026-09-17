//! Shell Session Safety and Immediate Client Revocation.
//!
//! Hard Invariants & Specifications (docs/spec/09-ticket-state-and-shell-safety.md, ADR-004):
//! - Shell authorization is strictly bound to client-owned Ticket AccessEpoch.
//! - Client close/revoke is safety-critical:
//!   1. Atomically invalidate local AccessEpoch.
//!   2. Terminate active shell immediately via revocation handle.
//! - Anti-resurrection guarantee: Even if Admin tombstones an old client close event,
//!   the client daemon MUST NOT treat the old AccessEpoch as valid again.
//!   Only a new client-signed reopen creates valid access.

use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

use crate::canonical::events::safety::TicketSafetyState;
use crate::canonical::shell::challenge::ShellAuthError;
use crate::canonical::types::{AccessEpoch, TicketId};

/// Thread-safe guard holding immediate revocation capability for an active shell session.
#[derive(Debug, Clone)]
pub struct SessionRevocationGuard {
    revoked: Arc<AtomicBool>,
    token: CancellationToken,
}

impl Default for SessionRevocationGuard {
    fn default() -> Self {
        Self::new()
    }
}

impl SessionRevocationGuard {
    pub fn new() -> Self {
        Self {
            revoked: Arc::new(AtomicBool::new(false)),
            token: CancellationToken::new(),
        }
    }

    /// Returns true if this session has been revoked by the client.
    pub fn is_revoked(&self) -> bool {
        self.revoked.load(Ordering::SeqCst)
    }

    /// Returns the async cancellation token for `tokio::select!` loops.
    pub fn cancellation_token(&self) -> CancellationToken {
        self.token.clone()
    }

    /// Atomically revokes the shell session immediately.
    ///
    /// This causes any active PTY session to terminate synchronously without waiting for network RPCs.
    pub fn revoke(&self, _reason: &str) {
        self.revoked.store(true, Ordering::SeqCst);
        self.token.cancel();
    }
}

/// Registry of client AccessEpochs with permanent local anti-resurrection tracking.
#[derive(Debug, Default, Clone)]
pub struct EpochRegistry {
    /// Currently active epoch per ticket.
    active_epochs: HashMap<TicketId, AccessEpoch>,
    /// Permanently invalidated epochs that can NEVER be resurrected by remote events or tombstones.
    revoked_epochs: HashSet<AccessEpoch>,
}

impl EpochRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers a new active epoch for a ticket.
    ///
    /// Fails if the epoch has previously been revoked (anti-resurrection invariant).
    pub fn register_epoch(
        &mut self,
        ticket_id: TicketId,
        epoch: AccessEpoch,
    ) -> Result<(), ShellAuthError> {
        if self.revoked_epochs.contains(&epoch) {
            return Err(ShellAuthError::EpochRevoked(epoch));
        }
        self.active_epochs.insert(ticket_id, epoch);
        Ok(())
    }

    /// Permanently invalidates an epoch locally.
    pub fn invalidate_epoch(&mut self, ticket_id: &TicketId, epoch: &AccessEpoch) {
        self.revoked_epochs.insert(*epoch);
        if let Some(current) = self.active_epochs.get(ticket_id) {
            if current == epoch {
                self.active_epochs.remove(ticket_id);
            }
        }
    }

    /// Validates whether an epoch is currently active and has not been revoked.
    pub fn is_epoch_valid(&self, ticket_id: &TicketId, epoch: &AccessEpoch) -> bool {
        if self.revoked_epochs.contains(epoch) {
            return false;
        }
        self.active_epochs.get(ticket_id) == Some(epoch)
    }
}

/// Gate validating shell session authorization before starting PTY child process.
pub struct SessionSafetyGate;

impl SessionSafetyGate {
    /// Authorizes a shell request against ticket state, epoch registry, and presented epoch.
    pub fn authorize_session(
        ticket_safety: &TicketSafetyState,
        epoch_registry: &EpochRegistry,
        presented_epoch: &AccessEpoch,
    ) -> Result<SessionRevocationGuard, ShellAuthError> {
        // 1. Check ticket lifecycle permits work (Open or InProgress)
        if !ticket_safety.lifecycle.permits_work() || !ticket_safety.access_valid {
            return Err(ShellAuthError::TicketStateClosed(ticket_safety.ticket_id));
        }

        // 2. Check local epoch validity against anti-resurrection registry
        if !epoch_registry.is_epoch_valid(&ticket_safety.ticket_id, presented_epoch) {
            return Err(ShellAuthError::EpochRevoked(*presented_epoch));
        }

        // 3. Check ticket safety state epoch matches presented epoch
        if ticket_safety.access_epoch != *presented_epoch {
            return Err(ShellAuthError::EpochMismatch(
                ticket_safety.access_epoch,
                *presented_epoch,
            ));
        }

        Ok(SessionRevocationGuard::new())
    }
}
