//! Canonical Shell Safety, Challenge Authentication, and Immediate Revocation.
//!
//! Enforces ADR-007: Owner/Operator session challenges and lifecycle-based
//! shell cancellation.

pub mod challenge;
pub mod session;

#[cfg(any())]
mod tests;

pub use challenge::{ShellAuthError, ShellAuthResponse, ShellChallenge, SHELL_CHALLENGE_DOMAIN};
pub use session::{SessionRevocationGuard, SessionSafetyGate};
