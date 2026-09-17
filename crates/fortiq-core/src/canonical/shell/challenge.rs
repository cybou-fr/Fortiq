//! Canonical Shell Authentication and Challenge Protocol.
//!
//! Hard Invariants & Specifications (docs/spec/09-ticket-state-and-shell-safety.md):
//! - Operator presents authenticated session challenge response signed with Operator Key.
//! - Challenge binds `ticket_id`, `session_id`, and `challenge_nonce`.
//! - Domain separation: `b"FORTIQ-SHELL-CHALLENGE-v2:"`.

use crate::canonical::codec::serde_bytes;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId, TicketId};
use rand_core::{OsRng, RngCore};
use serde::{Deserialize, Serialize};
use thiserror::Error;

/// Domain separation string for shell authentication challenge.
pub const SHELL_CHALLENGE_DOMAIN: &[u8] = b"FORTIQ-SHELL-CHALLENGE-v2:";

#[derive(Error, Debug, PartialEq, Eq)]
pub enum ShellAuthError {
    #[error("cryptographic signing error: {0}")]
    Signing(String),
    #[error("signature verification failed: {0}")]
    VerificationFailed(String),
    #[error("ticket {0} does not permit shell access: state is closed or inactive")]
    TicketStateClosed(TicketId),
    #[error("session revoked immediately by client: {0}")]
    SessionRevoked(String),
}

impl From<SigningError> for ShellAuthError {
    fn from(e: SigningError) -> Self {
        ShellAuthError::Signing(e.to_string())
    }
}

/// Challenge issued by the client node to an operator attempting to open a shell.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShellChallenge {
    pub session_id: [u8; 16],
    pub ticket_id: TicketId,
    pub challenge_nonce: [u8; 32],
}

impl ShellChallenge {
    /// Generates a fresh random challenge bound to a ticket.
    pub fn new(ticket_id: TicketId) -> Self {
        let mut session_id = [0u8; 16];
        let mut challenge_nonce = [0u8; 32];
        OsRng.fill_bytes(&mut session_id);
        OsRng.fill_bytes(&mut challenge_nonce);

        Self {
            session_id,
            ticket_id,
            challenge_nonce,
        }
    }

    /// Computes the domain-separated bytes to be signed by the operator.
    pub fn compute_signing_payload(&self) -> Vec<u8> {
        let mut payload = Vec::with_capacity(SHELL_CHALLENGE_DOMAIN.len() + 16 + 16 + 32);
        payload.extend_from_slice(SHELL_CHALLENGE_DOMAIN);
        payload.extend_from_slice(&self.session_id);
        payload.extend_from_slice(self.ticket_id.as_bytes());
        payload.extend_from_slice(&self.challenge_nonce);
        payload
    }
}

/// Cryptographic response from the operator proving possession of authorized key.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShellAuthResponse {
    pub operator_key_id: KeyId,
    pub operator_entity: EntityId,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

impl ShellAuthResponse {
    /// Signs a client challenge with the operator's private key.
    pub fn create(
        signer: &impl Signer,
        operator_entity: EntityId,
        challenge: &ShellChallenge,
    ) -> Result<Self, ShellAuthError> {
        let payload = challenge.compute_signing_payload();
        let signature = signer.sign(&payload)?;
        Ok(Self {
            operator_key_id: signer.key_id(),
            operator_entity,
            signature,
        })
    }

    /// Verifies that the signature in this response correctly signs the challenge payload.
    pub fn verify(
        &self,
        verifier: &impl Verifier,
        challenge: &ShellChallenge,
    ) -> Result<(), ShellAuthError> {
        let payload = challenge.compute_signing_payload();
        verifier
            .verify(&payload, &self.signature)
            .map_err(|e| ShellAuthError::VerificationFailed(e.to_string()))
    }
}
