//! Mnemonic Unlock and Key Derivation for Portable Operators.
//!
//! Hard Invariants & Specifications (docs/spec/03-genesis-owner-key-lifecycle.md):
//! - 256 bits of mnemonic entropy.
//! - Derives independent secrets with domain separation:
//!   - OwnerRootSigningSeed
//!   - OwnerSegmentMasterSeed
//!   - OperatorSessionSeed
//! - Never use mnemonic bytes directly as protocol keys.
//! - Root signing key signs session certificates; root secret is wiped immediately.
//! - OwnerSegmentMasterSeed remains only in unlocked memory session.

use hkdf::Hkdf;
use sha2::Sha256;
use thiserror::Error;

use crate::canonical::crypto::keys::{
    MnemonicEntropy, OperatorSessionSeed, OwnerRootSigningSeed, OwnerSegmentMasterSeed,
};

pub const MNEMONIC_KDF_SALT: &[u8] = b"FORTIQ-MNEMONIC-KDF-SALT-v1:";
pub const ROOT_SIGNING_INFO: &[u8] = b"FORTIQ-OWNER-ROOT-SIGNING-v1:";
pub const SEGMENT_MASTER_INFO: &[u8] = b"FORTIQ-OWNER-SEGMENT-MASTER-v1:";
pub const OPERATOR_SESSION_INFO: &[u8] = b"FORTIQ-OPERATOR-SESSION-v1:";

#[derive(Error, Debug, PartialEq, Eq)]
pub enum MnemonicError {
    #[error("HKDF key derivation failed: {0}")]
    DerivationFailed(String),
}

/// Helper for deriving domain-separated keys from raw mnemonic entropy.
pub struct MnemonicDeriver<'a> {
    entropy: &'a MnemonicEntropy,
}

impl<'a> MnemonicDeriver<'a> {
    pub fn new(entropy: &'a MnemonicEntropy) -> Self {
        Self { entropy }
    }

    /// Derives the OwnerRootSigningSeed used strictly for genesis and session certificate issuance.
    pub fn derive_root_signing_seed(&self) -> Result<OwnerRootSigningSeed, MnemonicError> {
        let hk = Hkdf::<Sha256>::new(Some(MNEMONIC_KDF_SALT), self.entropy.as_bytes());
        let mut okm = [0u8; 32];
        hk.expand(ROOT_SIGNING_INFO, &mut okm)
            .map_err(|e| MnemonicError::DerivationFailed(e.to_string()))?;
        Ok(OwnerRootSigningSeed::new(okm))
    }

    /// Derives the OwnerSegmentMasterSeed used to derive per-client segment keys on demand.
    pub fn derive_segment_master_seed(&self) -> Result<OwnerSegmentMasterSeed, MnemonicError> {
        let hk = Hkdf::<Sha256>::new(Some(MNEMONIC_KDF_SALT), self.entropy.as_bytes());
        let mut okm = [0u8; 32];
        hk.expand(SEGMENT_MASTER_INFO, &mut okm)
            .map_err(|e| MnemonicError::DerivationFailed(e.to_string()))?;
        Ok(OwnerSegmentMasterSeed::new(okm))
    }

    /// Derives a temporary, ephemeral OperatorSessionSeed for signing routine ticket and chat records.
    pub fn derive_operator_session_seed(
        &self,
        session_nonce: &[u8; 16],
    ) -> Result<OperatorSessionSeed, MnemonicError> {
        let hk = Hkdf::<Sha256>::new(Some(MNEMONIC_KDF_SALT), self.entropy.as_bytes());
        let mut info = Vec::with_capacity(OPERATOR_SESSION_INFO.len() + 16);
        info.extend_from_slice(OPERATOR_SESSION_INFO);
        info.extend_from_slice(session_nonce);

        let mut okm = [0u8; 32];
        hk.expand(&info, &mut okm)
            .map_err(|e| MnemonicError::DerivationFailed(e.to_string()))?;
        Ok(OperatorSessionSeed::new(okm))
    }
}
