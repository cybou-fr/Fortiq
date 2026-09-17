//! Operator Session Certificates for Portable Operators.
//!
//! Hard Invariants & Specifications (docs/spec/03-genesis-owner-key-lifecycle.md):
//! - Owner Root signing key signs Operator Session Certificates.
//! - The root secret is wiped from memory best-effort after session creation.
//! - Session certificates bind ephemeral operator keys to owner identity, specific host entity,
//!   and session public key with bounded lifetime (max 24h TTL) and replay prevention nonce.

use crate::canonical::codec::serde_bytes;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId, NetworkId, OwnerId};
use serde::{Deserialize, Serialize};
use thiserror::Error;

/// Domain separation string for operator session certificate signatures.
pub const SESSION_CERT_SIG_DOMAIN: &[u8] = b"FORTIQ-OPERATOR-SESSION-CERT-v2:";

/// Maximum allowed lifetime for an operator session certificate (24 hours).
pub const MAX_SESSION_TTL_SECS: u64 = 86_400;

#[derive(Error, Debug, PartialEq, Eq)]
pub enum CertificateError {
    #[error("session certificate not yet valid: not_before {0}, current time {1}")]
    NotYetValid(u64, u64),
    #[error("session certificate expired at {0}, current time {1}")]
    Expired(u64, u64),
    #[error("invalid session lifetime: duration {0}s exceeds max allowed {1}s")]
    TtlExceeded(u64, u64),
    #[error("invalid validity window: expires_at ({0}) precedes not_before ({1})")]
    InvalidValidityWindow(u64, u64),
    #[error("host entity mismatch: certificate issued to {expected}, presented by {got}")]
    HostMismatch { expected: EntityId, got: EntityId },
    #[error("missing required capability {0:#x}")]
    MissingCapability(u32),
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

/// Strongly-typed capability bitset for operator sessions.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Default)]
pub struct OperatorCapabilities(pub u32);

impl OperatorCapabilities {
    pub const NONE: u32 = 0;
    pub const READ: u32 = 1 << 0;
    pub const WRITE: u32 = 1 << 1;
    pub const SHELL_EXEC: u32 = 1 << 2;
    pub const FILE_TRANSFER: u32 = 1 << 3;
    pub const TICKET_MANAGE: u32 = 1 << 4;
    pub const DIAGNOSTICS: u32 = 1 << 5;
    pub const ADMIN: u32 = Self::READ
        | Self::WRITE
        | Self::SHELL_EXEC
        | Self::FILE_TRANSFER
        | Self::TICKET_MANAGE
        | Self::DIAGNOSTICS;

    pub fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    pub fn has(&self, flag: u32) -> bool {
        (self.0 & flag) == flag
    }

    pub fn with(&self, flag: u32) -> Self {
        Self(self.0 | flag)
    }

    /// Parses string capability names into standard bitset.
    pub fn from_names<I, S>(names: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let mut bits = 0u32;
        for name in names {
            match name.as_ref().to_lowercase().as_str() {
                "admin" => bits |= Self::ADMIN,
                "read" | "ticket:read" => bits |= Self::READ,
                "write" | "ticket:write" => bits |= Self::WRITE,
                "shell" | "shell:execute" => bits |= Self::SHELL_EXEC,
                "file" | "file:transfer" => bits |= Self::FILE_TRANSFER,
                "ticket" | "ticket:manage" => bits |= Self::TICKET_MANAGE,
                "diag" | "diagnostics" => bits |= Self::DIAGNOSTICS,
                _ => {}
            }
        }
        Self(bits)
    }

    /// Formats active capability names.
    pub fn to_names(&self) -> Vec<String> {
        let mut names = Vec::new();
        if self.has(Self::READ) {
            names.push("read".into());
        }
        if self.has(Self::WRITE) {
            names.push("write".into());
        }
        if self.has(Self::SHELL_EXEC) {
            names.push("shell".into());
        }
        if self.has(Self::FILE_TRANSFER) {
            names.push("file".into());
        }
        if self.has(Self::TICKET_MANAGE) {
            names.push("ticket".into());
        }
        if self.has(Self::DIAGNOSTICS) {
            names.push("diagnostics".into());
        }
        names
    }
}

/// Certificate delegating temporary operator authority from Owner Root to an ephemeral operator session.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OperatorSessionCertificate {
    pub network_id: NetworkId,
    pub owner_id: OwnerId,
    pub host_entity: EntityId,
    pub operator_entity: EntityId,
    pub operator_key_id: KeyId,
    pub session_pubkey: [u8; 32],
    pub capabilities: OperatorCapabilities,
    pub not_before: u64,
    pub expires_at: u64,
    pub nonce: [u8; 16],
    #[serde(with = "serde_bytes")]
    pub owner_signature: Vec<u8>,
}

impl OperatorSessionCertificate {
    /// Computes the domain-separated bytes to be signed by the Owner Root.
    #[allow(clippy::too_many_arguments)]
    pub fn compute_signing_payload(
        network_id: &NetworkId,
        owner_id: &OwnerId,
        host_entity: &EntityId,
        operator_entity: &EntityId,
        operator_key_id: &KeyId,
        session_pubkey: &[u8; 32],
        capabilities: OperatorCapabilities,
        not_before: u64,
        expires_at: u64,
        nonce: &[u8; 16],
    ) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(SESSION_CERT_SIG_DOMAIN);
        payload.extend_from_slice(network_id.as_bytes());
        payload.extend_from_slice(owner_id.as_bytes());
        payload.extend_from_slice(host_entity.as_bytes());
        payload.extend_from_slice(operator_entity.as_bytes());
        payload.extend_from_slice(operator_key_id.as_bytes());
        payload.extend_from_slice(session_pubkey);
        payload.extend_from_slice(&capabilities.0.to_be_bytes());
        payload.extend_from_slice(&not_before.to_be_bytes());
        payload.extend_from_slice(&expires_at.to_be_bytes());
        payload.extend_from_slice(nonce);
        payload
    }

    /// Issues and signs a new `OperatorSessionCertificate` using the Owner Root signer.
    #[allow(clippy::too_many_arguments)]
    pub fn issue(
        network_id: NetworkId,
        owner_id: OwnerId,
        host_entity: EntityId,
        operator_entity: EntityId,
        operator_key_id: KeyId,
        session_pubkey: [u8; 32],
        capabilities: OperatorCapabilities,
        not_before: u64,
        expires_at: u64,
        nonce: [u8; 16],
        owner_root_signer: &impl Signer,
    ) -> Result<Self, CertificateError> {
        if expires_at < not_before {
            return Err(CertificateError::InvalidValidityWindow(
                expires_at, not_before,
            ));
        }
        let duration = expires_at - not_before;
        if duration > MAX_SESSION_TTL_SECS {
            return Err(CertificateError::TtlExceeded(
                duration,
                MAX_SESSION_TTL_SECS,
            ));
        }

        let payload = Self::compute_signing_payload(
            &network_id,
            &owner_id,
            &host_entity,
            &operator_entity,
            &operator_key_id,
            &session_pubkey,
            capabilities,
            not_before,
            expires_at,
            &nonce,
        );
        let owner_signature = owner_root_signer.sign(&payload)?;
        Ok(Self {
            network_id,
            owner_id,
            host_entity,
            operator_entity,
            operator_key_id,
            session_pubkey,
            capabilities,
            not_before,
            expires_at,
            nonce,
            owner_signature,
        })
    }

    /// Verifies the certificate signature and checks expiration against current time.
    pub fn verify(
        &self,
        verifier: &impl Verifier,
        current_time: u64,
    ) -> Result<(), CertificateError> {
        if self.expires_at < self.not_before {
            return Err(CertificateError::InvalidValidityWindow(
                self.expires_at,
                self.not_before,
            ));
        }
        let duration = self.expires_at - self.not_before;
        if duration > MAX_SESSION_TTL_SECS {
            return Err(CertificateError::TtlExceeded(
                duration,
                MAX_SESSION_TTL_SECS,
            ));
        }
        if current_time < self.not_before {
            return Err(CertificateError::NotYetValid(self.not_before, current_time));
        }
        if self.expires_at < current_time {
            return Err(CertificateError::Expired(self.expires_at, current_time));
        }
        let payload = Self::compute_signing_payload(
            &self.network_id,
            &self.owner_id,
            &self.host_entity,
            &self.operator_entity,
            &self.operator_key_id,
            &self.session_pubkey,
            self.capabilities,
            self.not_before,
            self.expires_at,
            &self.nonce,
        );
        verifier
            .verify(&payload, &self.owner_signature)
            .map_err(|e| CertificateError::VerificationFailed(e.to_string()))
    }
}
