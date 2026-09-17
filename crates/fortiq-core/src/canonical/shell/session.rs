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

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

use crate::canonical::events::safety::TicketSafetyState;
use crate::canonical::shell::challenge::ShellAuthError;

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

/// Gate validating shell session authorization before starting PTY child process.
pub struct SessionSafetyGate;

impl SessionSafetyGate {
    /// Authorizes a shell request against ticket lifecycle state.
    pub fn authorize_session(
        ticket_safety: &TicketSafetyState,
    ) -> Result<SessionRevocationGuard, ShellAuthError> {
        if !ticket_safety.lifecycle.permits_work() {
            return Err(ShellAuthError::TicketStateClosed(ticket_safety.ticket_id));
        }

        Ok(SessionRevocationGuard::new())
    }
}
