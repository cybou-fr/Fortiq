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
use sha2::{Digest, Sha256};
use std::sync::OnceLock;
use thiserror::Error;

use crate::canonical::crypto::keys::{
    MnemonicEntropy, OperatorSessionSeed, OwnerRootSigningSeed, OwnerSegmentMasterSeed,
};

pub const MNEMONIC_KDF_SALT: &[u8] = b"FORTIQ-MNEMONIC-KDF-SALT-v1:";
pub const ROOT_SIGNING_INFO: &[u8] = b"FORTIQ-OWNER-ROOT-SIGNING-v1:";
pub const SEGMENT_MASTER_INFO: &[u8] = b"FORTIQ-OWNER-SEGMENT-MASTER-v1:";
pub const OPERATOR_SESSION_INFO: &[u8] = b"FORTIQ-OPERATOR-SESSION-v1:";

static WORDLIST: OnceLock<Vec<&'static str>> = OnceLock::new();

/// Returns the official BIP-39 English 2048-word dictionary.
pub fn get_wordlist() -> &'static [&'static str] {
    WORDLIST.get_or_init(|| {
        include_str!("bip39_english.txt")
            .lines()
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .collect()
    })
}

/// Resolves a word to its 11-bit dictionary index [0, 2047] via binary search.
pub fn word_to_index(word: &str) -> Option<u16> {
    let words = get_wordlist();
    words.binary_search(&word).ok().map(|idx| idx as u16)
}

#[derive(Error, Debug, PartialEq, Eq)]
pub enum MnemonicError {
    #[error("invalid word count: expected 24 words, got {0}")]
    InvalidWordCount(usize),
    #[error("unknown word in mnemonic phrase: '{0}'")]
    UnknownWord(String),
    #[error("invalid mnemonic checksum")]
    InvalidChecksum,
    #[error("HKDF key derivation failed: {0}")]
    DerivationFailed(String),
}

/// Converts a 24-word BIP-39 mnemonic phrase into validated 256-bit MnemonicEntropy.
pub fn parse_mnemonic_phrase(phrase: &str) -> Result<MnemonicEntropy, MnemonicError> {
    let words: Vec<&str> = phrase.split_whitespace().collect();
    if words.len() != 24 {
        return Err(MnemonicError::InvalidWordCount(words.len()));
    }

    let mut indices = [0u16; 24];
    for (i, word) in words.iter().enumerate() {
        let idx =
            word_to_index(word).ok_or_else(|| MnemonicError::UnknownWord(word.to_string()))?;
        indices[i] = idx;
    }

    let mut bits = [false; 264];
    for (i, &word_idx) in indices.iter().enumerate() {
        for bit in 0..11 {
            bits[i * 11 + bit] = ((word_idx >> (10 - bit)) & 1) == 1;
        }
    }

    let mut bytes = [0u8; 33];
    for (i, byte) in bytes.iter_mut().enumerate() {
        let mut b = 0u8;
        for bit in 0..8 {
            if bits[i * 8 + bit] {
                b |= 1 << (7 - bit);
            }
        }
        *byte = b;
    }

    let mut entropy_bytes = [0u8; 32];
    entropy_bytes.copy_from_slice(&bytes[..32]);
    let expected_checksum = bytes[32];

    let mut hasher = Sha256::new();
    hasher.update(entropy_bytes);
    let hash: [u8; 32] = hasher.finalize().into();
    let actual_checksum = hash[0];

    if actual_checksum != expected_checksum {
        return Err(MnemonicError::InvalidChecksum);
    }

    Ok(MnemonicEntropy::new(entropy_bytes))
}

/// Encodes a 256-bit MnemonicEntropy into a 24-word BIP-39 mnemonic phrase.
pub fn entropy_to_mnemonic(entropy: &MnemonicEntropy) -> String {
    let entropy_bytes = entropy.as_bytes();
    let mut hasher = Sha256::new();
    hasher.update(entropy_bytes);
    let hash: [u8; 32] = hasher.finalize().into();
    let checksum = hash[0];

    let mut bytes = [0u8; 33];
    bytes[..32].copy_from_slice(entropy_bytes);
    bytes[32] = checksum;

    let mut bits = [false; 264];
    for (i, &b) in bytes.iter().enumerate() {
        for bit in 0..8 {
            bits[i * 8 + bit] = ((b >> (7 - bit)) & 1) == 1;
        }
    }

    let wordlist = get_wordlist();
    let mut words = Vec::with_capacity(24);
    for i in 0..24 {
        let mut idx = 0u16;
        for bit in 0..11 {
            if bits[i * 11 + bit] {
                idx |= 1 << (10 - bit);
            }
        }
        words.push(wordlist[idx as usize]);
    }

    words.join(" ")
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
