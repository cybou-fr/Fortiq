//! Cryptographic Provider layer for FORTIQ Canonical Architecture v3.
//!
//! Provides symmetric AEAD (ChaCha20Poly1305), one payload / N recipient envelopes,
//! deterministic per-segment key derivation, and memory zeroization.

pub mod keys;
pub mod provider;

#[cfg(test)]
mod tests;

pub use keys::{DataEncryptionKey, DerivedSegmentSecret, MnemonicEntropy, OwnerSegmentMasterSeed};
pub use provider::{
    CryptoError, CryptoProvider, StandardCryptoProvider, ENVELOPE_WRAP_DOMAIN,
    OPERATOR_SEGMENT_KDF_SALT,
};
