//! Streaming AEAD for Large Attachments.
//!
//! Hard Invariants & Specifications (docs/spec/13-chat-and-files.md):
//! - Fixed-size plaintext chunks.
//! - AEAD per chunk with unique nonce/AAD.
//! - Nonce must be unique under FileKey: 8-byte random file nonce prefix + 4-byte chunk index.
//! - AAD binds the chunk to its attachment ID, sequence number, and is_last indicator.
//! - ChaCha20Poly1305 with 16-byte authentication tag per chunk.

use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::{ChaCha20Poly1305, Key, Nonce};
use thiserror::Error;

use crate::canonical::files::key::FileKey;
use crate::canonical::types::ObjectId;

/// Default plaintext chunk size for streaming AEAD (64 KiB).
pub const DEFAULT_FILE_CHUNK_SIZE: usize = 64 * 1024;

/// Domain separator for chunk AAD binding.
pub const FILE_CHUNK_AAD_DOMAIN: &[u8] = b"FORTIQ-FILE-CHUNK-v1:";

#[derive(Error, Debug, PartialEq, Eq)]
pub enum FileStreamError {
    #[error("chunk encryption failed")]
    EncryptionFailed,
    #[error("chunk decryption failed: authentication tag or AAD mismatch")]
    DecryptionFailed,
    #[error("unexpected chunk sequence: expected {0}, got {1}")]
    SequenceMismatch(u32, u32),
    #[error("premature end of file stream: expected is_last flag on last chunk")]
    PrematureEndOfStream,
    #[error("stream already finalized: received chunk {0} after last chunk")]
    StreamAlreadyFinalized(u32),
}

/// Computes the 12-byte ChaCha20Poly1305 nonce for a file chunk.
/// Format: `nonce_prefix[8] || chunk_seq[4] (big-endian)`.
pub fn compute_chunk_nonce(nonce_prefix: &[u8; 8], chunk_seq: u32) -> [u8; 12] {
    let mut nonce = [0u8; 12];
    nonce[..8].copy_from_slice(nonce_prefix);
    nonce[8..].copy_from_slice(&chunk_seq.to_be_bytes());
    nonce
}

/// Computes the domain-separated authenticated additional data (AAD) for a chunk.
pub fn compute_chunk_aad(attachment_id: &ObjectId, chunk_seq: u32, is_last: bool) -> Vec<u8> {
    let mut aad = Vec::with_capacity(FILE_CHUNK_AAD_DOMAIN.len() + 32 + 4 + 1);
    aad.extend_from_slice(FILE_CHUNK_AAD_DOMAIN);
    aad.extend_from_slice(attachment_id.as_bytes());
    aad.extend_from_slice(&chunk_seq.to_be_bytes());
    aad.push(if is_last { 1 } else { 0 });
    aad
}

/// Encrypts a single plaintext chunk with the given FileKey and chunk metadata.
pub fn encrypt_chunk(
    file_key: &FileKey,
    nonce_prefix: &[u8; 8],
    chunk_seq: u32,
    is_last: bool,
    attachment_id: &ObjectId,
    plaintext: &[u8],
) -> Result<Vec<u8>, FileStreamError> {
    let nonce_bytes = compute_chunk_nonce(nonce_prefix, chunk_seq);
    let nonce = Nonce::from_slice(&nonce_bytes);
    let aad = compute_chunk_aad(attachment_id, chunk_seq, is_last);

    let cipher = ChaCha20Poly1305::new(Key::from_slice(file_key.as_bytes()));
    let payload = Payload {
        msg: plaintext,
        aad: &aad,
    };

    cipher
        .encrypt(nonce, payload)
        .map_err(|_| FileStreamError::EncryptionFailed)
}

/// Decrypts a single ciphertext chunk with the given FileKey and chunk metadata.
pub fn decrypt_chunk(
    file_key: &FileKey,
    nonce_prefix: &[u8; 8],
    chunk_seq: u32,
    is_last: bool,
    attachment_id: &ObjectId,
    ciphertext: &[u8],
) -> Result<Vec<u8>, FileStreamError> {
    let nonce_bytes = compute_chunk_nonce(nonce_prefix, chunk_seq);
    let nonce = Nonce::from_slice(&nonce_bytes);
    let aad = compute_chunk_aad(attachment_id, chunk_seq, is_last);

    let cipher = ChaCha20Poly1305::new(Key::from_slice(file_key.as_bytes()));
    let payload = Payload {
        msg: ciphertext,
        aad: &aad,
    };

    cipher
        .decrypt(nonce, payload)
        .map_err(|_| FileStreamError::DecryptionFailed)
}

/// Stateful streaming encryptor for splitting an arbitrary file into encrypted AEAD chunks.
pub struct FileStreamEncryptor {
    file_key: FileKey,
    attachment_id: ObjectId,
    nonce_prefix: [u8; 8],
    chunk_size: usize,
    next_chunk_seq: u32,
    finalized: bool,
}

impl FileStreamEncryptor {
    pub fn new(
        file_key: FileKey,
        attachment_id: ObjectId,
        nonce_prefix: [u8; 8],
        chunk_size: usize,
    ) -> Self {
        Self {
            file_key,
            attachment_id,
            nonce_prefix,
            chunk_size: chunk_size.max(1),
            next_chunk_seq: 0,
            finalized: false,
        }
    }

    /// Encrypts an entire plaintext byte slice into a vector of sequential ciphertext chunks.
    pub fn encrypt_all(&mut self, plaintext: &[u8]) -> Result<Vec<Vec<u8>>, FileStreamError> {
        let mut ciphertexts = Vec::new();

        if plaintext.is_empty() {
            let ct = encrypt_chunk(
                &self.file_key,
                &self.nonce_prefix,
                0,
                true,
                &self.attachment_id,
                &[],
            )?;
            self.finalized = true;
            self.next_chunk_seq = 1;
            return Ok(vec![ct]);
        }

        let mut offset = 0;
        while offset < plaintext.len() {
            let end = (offset + self.chunk_size).min(plaintext.len());
            let is_last = end == plaintext.len();
            let chunk_data = &plaintext[offset..end];

            let ct = encrypt_chunk(
                &self.file_key,
                &self.nonce_prefix,
                self.next_chunk_seq,
                is_last,
                &self.attachment_id,
                chunk_data,
            )?;

            ciphertexts.push(ct);
            self.next_chunk_seq += 1;
            offset = end;

            if is_last {
                self.finalized = true;
                break;
            }
        }

        Ok(ciphertexts)
    }
}

/// Stateful streaming decryptor verifying chunk order, integrity, and termination.
pub struct FileStreamDecryptor {
    file_key: FileKey,
    attachment_id: ObjectId,
    nonce_prefix: [u8; 8],
    next_chunk_seq: u32,
    finalized: bool,
}

impl FileStreamDecryptor {
    pub fn new(file_key: FileKey, attachment_id: ObjectId, nonce_prefix: [u8; 8]) -> Self {
        Self {
            file_key,
            attachment_id,
            nonce_prefix,
            next_chunk_seq: 0,
            finalized: false,
        }
    }

    /// Decrypts a sequence of ciphertext chunks and reassembles the complete plaintext.
    pub fn decrypt_all(&mut self, chunks: &[Vec<u8>]) -> Result<Vec<u8>, FileStreamError> {
        let mut plaintext = Vec::new();
        let total = chunks.len();

        for (i, ct) in chunks.iter().enumerate() {
            if self.finalized {
                return Err(FileStreamError::StreamAlreadyFinalized(self.next_chunk_seq));
            }

            let is_last = i + 1 == total;
            let pt = decrypt_chunk(
                &self.file_key,
                &self.nonce_prefix,
                self.next_chunk_seq,
                is_last,
                &self.attachment_id,
                ct,
            )?;

            plaintext.extend_from_slice(&pt);
            self.next_chunk_seq += 1;

            if is_last {
                self.finalized = true;
            }
        }

        if !self.finalized && total > 0 {
            return Err(FileStreamError::PrematureEndOfStream);
        }

        Ok(plaintext)
    }
}
