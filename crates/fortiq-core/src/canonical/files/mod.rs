//! File Pipeline and Streaming AEAD for Large Attachments.
//!
//! Provides FileKey, Streaming AEAD per chunk, Attachment Manifests,
//! and Upload Resumption.

pub mod aead;
pub mod key;
pub mod manifest;
pub mod resume;

#[cfg(test)]
mod tests;

pub use aead::{
    compute_chunk_aad, compute_chunk_nonce, decrypt_chunk, encrypt_chunk, FileStreamDecryptor,
    FileStreamEncryptor, FileStreamError, DEFAULT_FILE_CHUNK_SIZE, FILE_CHUNK_AAD_DOMAIN,
};
pub use key::FileKey;
pub use manifest::{
    AttachmentManifest, AttachmentPlaintextMetadata, ManifestError, ATTACHMENT_META_AAD_DOMAIN,
};
pub use resume::{FileUploadSession, ResumeError};
