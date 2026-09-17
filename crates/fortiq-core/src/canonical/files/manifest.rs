//! Attachment Manifest and Encrypted Metadata.
//!
//! Hard Invariants & Specifications (docs/spec/13-chat-and-files.md, ADR-005):
//! - Sensitive metadata (filename, MIME, size, hash, sender) is encrypted.
//! - Outer manifest contains only what storage and distribution nodes need.
//! - RecipientEnvelopes allow authorized ticket counterparties to unwrap the FileKey.
//! - Integrates the underlying Reed-Solomon BlobManifest.

use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::{ChaCha20Poly1305, Key, Nonce};
use rand_core::{OsRng, RngCore};
use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::canonical::codec::{
    from_canonical_cbor, serde_bytes, to_canonical_cbor, CodecError, DecoderLimits,
};
use crate::canonical::files::key::FileKey;
use crate::canonical::records::{BlobManifest, RecipientEnvelope};
use crate::canonical::types::{
    BlobId, CryptoProfileId, EntityId, ObjectId, StorageClass, TicketId,
};

/// Domain separation for encrypted attachment metadata.
pub const ATTACHMENT_META_AAD_DOMAIN: &[u8] = b"FORTIQ-ATTACHMENT-META-v1:";

#[derive(Error, Debug)]
pub enum ManifestError {
    #[error("CBOR serialization/deserialization failed: {0}")]
    Codec(#[from] CodecError),
    #[error("metadata encryption failed")]
    EncryptionFailed,
    #[error("metadata decryption failed: authentication tag or key mismatch")]
    DecryptionFailed,
    #[error("payload too short: {0} bytes (less than 12-byte nonce + 16-byte tag)")]
    PayloadTooShort(usize),
}

/// Sensitive inner metadata of an attachment, encrypted at rest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttachmentPlaintextMetadata {
    pub filename: String,
    pub mime_type: String,
    pub plaintext_size: u64,
    pub plaintext_blake3_hash: [u8; 32],
    pub sender: EntityId,
    pub ticket_id: TicketId,
    pub created_at: u64,
}

impl AttachmentPlaintextMetadata {
    /// Computes the AAD binding for metadata encryption.
    pub fn compute_aad(attachment_id: &ObjectId) -> Vec<u8> {
        let mut aad = Vec::with_capacity(ATTACHMENT_META_AAD_DOMAIN.len() + 32);
        aad.extend_from_slice(ATTACHMENT_META_AAD_DOMAIN);
        aad.extend_from_slice(attachment_id.as_bytes());
        aad
    }

    /// Encrypts inner metadata using the attachment's `FileKey`.
    /// Returns `nonce[12] || ciphertext || tag[16]`.
    pub fn seal(
        &self,
        file_key: &FileKey,
        attachment_id: &ObjectId,
    ) -> Result<Vec<u8>, ManifestError> {
        let plaintext_cbor = to_canonical_cbor(self)?;
        let aad = Self::compute_aad(attachment_id);

        let mut nonce_bytes = [0u8; 12];
        OsRng.fill_bytes(&mut nonce_bytes);
        let nonce = Nonce::from_slice(&nonce_bytes);

        let cipher = ChaCha20Poly1305::new(Key::from_slice(file_key.as_bytes()));
        let payload = Payload {
            msg: &plaintext_cbor,
            aad: &aad,
        };

        let ciphertext = cipher
            .encrypt(nonce, payload)
            .map_err(|_| ManifestError::EncryptionFailed)?;

        let mut result = Vec::with_capacity(12 + ciphertext.len());
        result.extend_from_slice(&nonce_bytes);
        result.extend_from_slice(&ciphertext);
        Ok(result)
    }

    /// Decrypts inner metadata from `nonce[12] || ciphertext || tag[16]`.
    pub fn open(
        encrypted_metadata: &[u8],
        file_key: &FileKey,
        attachment_id: &ObjectId,
    ) -> Result<Self, ManifestError> {
        if encrypted_metadata.len() < 28 {
            return Err(ManifestError::PayloadTooShort(encrypted_metadata.len()));
        }

        let nonce = Nonce::from_slice(&encrypted_metadata[..12]);
        let ciphertext = &encrypted_metadata[12..];
        let aad = Self::compute_aad(attachment_id);

        let cipher = ChaCha20Poly1305::new(Key::from_slice(file_key.as_bytes()));
        let payload = Payload {
            msg: ciphertext,
            aad: &aad,
        };

        let plaintext_cbor = cipher
            .decrypt(nonce, payload)
            .map_err(|_| ManifestError::DecryptionFailed)?;

        let metadata: Self = from_canonical_cbor(&plaintext_cbor, DecoderLimits::DEFAULT)?;
        Ok(metadata)
    }
}

/// Complete canonical manifest for a distributed attachment.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttachmentManifest {
    pub attachment_id: ObjectId,
    pub blob_id: BlobId,
    pub ticket_id: TicketId,
    pub storage_class: StorageClass,
    pub crypto_profile: CryptoProfileId,
    pub nonce_prefix: [u8; 8],
    pub total_chunks: u32,
    pub total_ciphertext_bytes: u64,
    #[serde(with = "serde_bytes")]
    pub encrypted_metadata: Vec<u8>,
    pub recipient_envelopes: Vec<RecipientEnvelope>,
    pub blob_manifest: BlobManifest,
}
