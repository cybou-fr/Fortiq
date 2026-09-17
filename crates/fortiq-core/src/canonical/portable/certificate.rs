//! Operator Session Certificates for Portable Operators.
//!
//! Hard Invariants & Specifications (docs/spec/03-genesis-owner-key-lifecycle.md):
//! - Owner Root signing key signs Operator Session Certificates.
//! - The root secret is wiped from memory best-effort after session creation.
//! - Session certificates bind ephemeral operator keys to owner identity with bounded lifetime.

use crate::canonical::codec::serde_bytes;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId, NetworkId, OwnerId};
use serde::{Deserialize, Serialize};
use thiserror::Error;

/// Domain separation string for operator session certificate signatures.
pub const SESSION_CERT_SIG_DOMAIN: &[u8] = b"FORTIQ-OPERATOR-SESSION-CERT-v1:";

#[derive(Error, Debug, PartialEq, Eq)]
pub enum CertificateError {
    #[error("session certificate expired at {0}, current time {1}")]
    Expired(u64, u64),
    #[error("cryptographic signing error: {0}")]
    Signing(String),
    #[error("certificate signature verification failed: {0}")]
    VerificationFailed(String),
}

impl From<SigningError> for CertificateError {
    fn from(e: SigningError) -> Self {
        CertificateError::Signing(e.to_string())
    }
}

/// Certificate delegating temporary operator authority from Owner Root to an ephemeral operator session.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OperatorSessionCertificate {
    pub network_id: NetworkId,
    pub owner_id: OwnerId,
    pub operator_key_id: KeyId,
    pub operator_entity: EntityId,
    pub capabilities: Vec<String>,
    pub issued_at: u64,
    pub expires_at: u64,
    #[serde(with = "serde_bytes")]
    pub owner_signature: Vec<u8>,
}

impl OperatorSessionCertificate {
    /// Computes the domain-separated bytes to be signed by the Owner Root.
    pub fn compute_signing_payload(
        network_id: &NetworkId,
        owner_id: &OwnerId,
        operator_key_id: &KeyId,
        operator_entity: &EntityId,
        capabilities: &[String],
        issued_at: u64,
        expires_at: u64,
    ) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(SESSION_CERT_SIG_DOMAIN);
        payload.extend_from_slice(network_id.as_bytes());
        payload.extend_from_slice(owner_id.as_bytes());
        payload.extend_from_slice(operator_key_id.as_bytes());
        payload.extend_from_slice(operator_entity.as_bytes());
        for cap in capabilities {
            payload.extend_from_slice(cap.as_bytes());
            payload.push(0x00);
        }
        payload.extend_from_slice(&issued_at.to_be_bytes());
        payload.extend_from_slice(&expires_at.to_be_bytes());
        payload
    }

    /// Issues and signs a new `OperatorSessionCertificate` using the Owner Root signer.
    #[allow(clippy::too_many_arguments)]
    pub fn issue(
        network_id: NetworkId,
        owner_id: OwnerId,
        operator_key_id: KeyId,
        operator_entity: EntityId,
        capabilities: Vec<String>,
        issued_at: u64,
        expires_at: u64,
        owner_root_signer: &impl Signer,
    ) -> Result<Self, CertificateError> {
        let payload = Self::compute_signing_payload(
            &network_id,
            &owner_id,
            &operator_key_id,
            &operator_entity,
            &capabilities,
            issued_at,
            expires_at,
        );
        let owner_signature = owner_root_signer.sign(&payload)?;
        Ok(Self {
            network_id,
            owner_id,
            operator_key_id,
            operator_entity,
            capabilities,
            issued_at,
            expires_at,
            owner_signature,
        })
    }

    /// Verifies the certificate signature and checks expiration against current time.
    pub fn verify(
        &self,
        verifier: &impl Verifier,
        current_time: u64,
    ) -> Result<(), CertificateError> {
        if self.expires_at < current_time {
            return Err(CertificateError::Expired(self.expires_at, current_time));
        }
        let payload = Self::compute_signing_payload(
            &self.network_id,
            &self.owner_id,
            &self.operator_key_id,
            &self.operator_entity,
            &self.capabilities,
            self.issued_at,
            self.expires_at,
        );
        verifier
            .verify(&payload, &self.owner_signature)
            .map_err(|e| CertificateError::VerificationFailed(e.to_string()))
    }
}
