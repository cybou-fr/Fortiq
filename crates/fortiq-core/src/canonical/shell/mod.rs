//! Canonical Shell Safety, Challenge Authentication, and Immediate Revocation.
//!
//! Enforces ADR-004 and spec 09: Owner/Operator session challenges,
//! Ticket AccessEpoch validation, and immediate client-side revocation.

pub mod challenge;
pub mod session;

#[cfg(test)]
mod tests;

pub use challenge::{ShellAuthError, ShellAuthResponse, ShellChallenge, SHELL_CHALLENGE_DOMAIN};
pub use session::{EpochRegistry, SessionRevocationGuard, SessionSafetyGate};
