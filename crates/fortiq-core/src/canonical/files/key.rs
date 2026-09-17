//! Symmetric FileKey for Attachment Encryption.
//!
//! Hard Invariants & Specifications (docs/spec/13-chat-and-files.md):
//! - FileKey is unique per attachment.
//! - Random 256-bit symmetric key.
//! - Automatically zeroizes memory on drop.
//! - Wrapped via RecipientEnvelopes for ticket participants.

use rand_core::{OsRng, RngCore};
use zeroize::{Zeroize, ZeroizeOnDrop};

/// 256-bit symmetric key dedicated to encrypting an attachment's contents.
#[derive(Clone, PartialEq, Eq, Zeroize, ZeroizeOnDrop)]
pub struct FileKey(pub [u8; 32]);

impl FileKey {
    /// Creates a FileKey from known key bytes.
    pub fn new(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    /// Generates a cryptographically secure random FileKey.
    pub fn generate() -> Self {
        let mut key = [0u8; 32];
        OsRng.fill_bytes(&mut key);
        Self(key)
    }

    pub fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}
