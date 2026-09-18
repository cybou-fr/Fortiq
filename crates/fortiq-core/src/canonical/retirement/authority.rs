//! Sovereign Cryptographic Authority Model.
//!
//! Hard Invariants (Phase 14 / docs/spec/21-implementation-roadmap.md):
//! - Deprecate and remove static configuration-derived `operator_peer_id` authority.
//! - Deprecate and remove permanent node roles (OPERATOR / MANAGED).
//! - Enforce dynamic cryptographic authority via Segment Capabilities,
//!   Owner-signed OperatorSessionCertificates, and ticket lifecycle state.

use thiserror::Error;

use crate::canonical::portable::certificate::OperatorSessionCertificate;
use crate::canonical::signing::Verifier;
use crate::canonical::types::EntityId;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum AuthorityError {
    #[error("Legacy static peer ID authorization is deprecated and rejected: {0}")]
    StaticPeerIdRejected(String),
    #[error("Missing or insufficient capability: required {required:#x}, held {held:#x}")]
    InsufficientCapability { required: u32, held: u32 },
    #[error("Invalid or expired session certificate: {0}")]
    InvalidCertificate(String),
}

/// Sovereign cryptographic authority resolver replacing static node roles.
pub struct CanonicalAuthorityResolver;

impl CanonicalAuthorityResolver {
    /// Evaluates operator session authorization via Owner-signed certificate.
    ///
    /// Explicitly replaces static `operator_peer_id` matching with cryptographic proof.
    pub fn verify_operator_session(
        cert: &OperatorSessionCertificate,
        owner_verifier: &impl Verifier,
        current_time: u64,
    ) -> Result<EntityId, AuthorityError> {
        cert.verify(owner_verifier, current_time)
            .map_err(|e| AuthorityError::InvalidCertificate(e.to_string()))?;

        Ok(cert.operator_entity)
    }

    /// Validates whether a participant possesses the required segment capability.
    pub fn verify_segment_capability(
        held_capabilities: u32,
        required_capability: u32,
    ) -> Result<(), AuthorityError> {
        if (held_capabilities & required_capability) == required_capability {
            Ok(())
        } else {
            Err(AuthorityError::InsufficientCapability {
                required: required_capability,
                held: held_capabilities,
            })
        }
    }

    /// Rejection guard for legacy static peer-id authorization.
    ///
    /// Fails with a hard error if an operation attempts to authorize solely by static peer ID string.
    pub fn reject_legacy_static_peer_id(static_peer_id: &str) -> Result<(), AuthorityError> {
        Err(AuthorityError::StaticPeerIdRejected(
            static_peer_id.to_string(),
        ))
    }
}
