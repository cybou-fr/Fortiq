//! Encrypted Ticket/Segment Snapshots for Fast Operator Catch-up.
//!
//! Hard Invariants & Specifications (docs/spec/12-sync-heads-anti-entropy.md, docs/spec/19-snapshots-search-cold-start.md):
//! - A fresh or catch-up operator downloads the latest encrypted snapshot and fetches tail packs only.
//! - Snapshots optimize cold start without replacing history.
//! - Encrypted via ChaCha20Poly1305 AEAD under segment key material.
//! - Signed by the authorizing entity (client or operator).
//! - Strict decoder limits applied upon deserialization.

use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::{ChaCha20Poly1305, Key, Nonce};
use rand_core::{OsRng, RngCore};
use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::canonical::codec::{
    from_canonical_cbor, serde_bytes, to_canonical_cbor, CodecError, DecoderLimits,
};
use crate::canonical::crypto::keys::DataEncryptionKey;
use crate::canonical::events::snapshot::TicketSnapshot;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{KeyId, ObjectId, SegmentId, TicketId};

/// Domain separation for snapshot signatures.
pub const SNAPSHOT_SIG_DOMAIN: &[u8] = b"FORTIQ_ENCRYPTED_SNAPSHOT_V1";

/// Maximum allowed encrypted snapshot size (4 MiB).
pub const MAX_SNAPSHOT_BYTES: usize = 4 * 1024 * 1024;

#[derive(Error, Debug)]
pub enum SnapshotSyncError {
    #[error("codec error: {0}")]
    Codec(#[from] CodecError),
    #[error("signing error: {0}")]
    Signing(#[from] SigningError),
    #[error("snapshot encryption error: {0}")]
    EncryptionFailed(String),
    #[error("snapshot decryption error: authentication tag or AAD mismatch")]
    DecryptionFailed,
    #[error("snapshot payload too short: {0} bytes (minimum 28 bytes required)")]
    PayloadTooShort(usize),
    #[error("snapshot exceeds maximum size {0} > {1}")]
    ExceedsMaxSize(usize, usize),
    #[error("ticket ID mismatch: snapshot container has {0}, inner view has {1}")]
    TicketIdMismatch(TicketId, TicketId),
}

/// Encrypted container for a `TicketSnapshot`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct EncryptedSnapshot {
    pub segment_id: SegmentId,
    pub ticket_id: TicketId,
    pub snapshot_seq: u64,
    pub frontier_head_packs: Vec<ObjectId>,
    pub created_at: u64,
    #[serde(with = "serde_bytes")]
    pub encrypted_payload: Vec<u8>,
    pub author_key_id: KeyId,
    #[serde(with = "serde_bytes")]
    pub signature: Vec<u8>,
}

impl EncryptedSnapshot {
    /// Computes authenticated additional data (AAD) bound to the AEAD encryption.
    pub fn compute_aad(
        segment_id: &SegmentId,
        ticket_id: &TicketId,
        snapshot_seq: u64,
        created_at: u64,
    ) -> Vec<u8> {
        let mut aad = Vec::with_capacity(32 + 16 + 8 + 8);
        aad.extend_from_slice(segment_id.as_bytes());
        aad.extend_from_slice(ticket_id.as_bytes());
        aad.extend_from_slice(&snapshot_seq.to_be_bytes());
        aad.extend_from_slice(&created_at.to_be_bytes());
        aad
    }

    /// Computes domain-separated signing payload.
    pub fn compute_signing_payload(
        segment_id: &SegmentId,
        ticket_id: &TicketId,
        author_key_id: &KeyId,
        snapshot_seq: u64,
        frontier_head_packs: &[ObjectId],
        created_at: u64,
        encrypted_payload: &[u8],
    ) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(SNAPSHOT_SIG_DOMAIN);
        payload.extend_from_slice(segment_id.as_bytes());
        payload.extend_from_slice(ticket_id.as_bytes());
        payload.extend_from_slice(author_key_id.as_bytes());
        payload.extend_from_slice(&snapshot_seq.to_be_bytes());
        payload.extend_from_slice(&(frontier_head_packs.len() as u32).to_be_bytes());
        for pack_id in frontier_head_packs {
            payload.extend_from_slice(pack_id.as_bytes());
        }
        payload.extend_from_slice(&created_at.to_be_bytes());
        payload.extend_from_slice(encrypted_payload);
        payload
    }

    /// Seals a `TicketSnapshot` into an `EncryptedSnapshot` with AEAD encryption and signature.
    pub fn seal(
        snapshot: &TicketSnapshot,
        segment_id: SegmentId,
        snapshot_seq: u64,
        segment_dek: &DataEncryptionKey,
        signer: &impl Signer,
    ) -> Result<Self, SnapshotSyncError> {
        let author_key_id = signer.key_id();
        let plaintext_cbor = to_canonical_cbor(snapshot)?;

        let aad = Self::compute_aad(
            &segment_id,
            &snapshot.ticket_id,
            snapshot_seq,
            snapshot.created_at,
        );

        // Encrypt with ChaCha20Poly1305
        let mut nonce_bytes = [0u8; 12];
        OsRng.fill_bytes(&mut nonce_bytes);
        let nonce = Nonce::from_slice(&nonce_bytes);

        let cipher = ChaCha20Poly1305::new(Key::from_slice(segment_dek.as_bytes()));
        let payload = Payload {
            msg: &plaintext_cbor,
            aad: &aad,
        };

        let ciphertext = cipher
            .encrypt(nonce, payload)
            .map_err(|e| SnapshotSyncError::EncryptionFailed(e.to_string()))?;

        let mut encrypted_payload = Vec::with_capacity(12 + ciphertext.len());
        encrypted_payload.extend_from_slice(&nonce_bytes);
        encrypted_payload.extend_from_slice(&ciphertext);

        if encrypted_payload.len() > MAX_SNAPSHOT_BYTES {
            return Err(SnapshotSyncError::ExceedsMaxSize(
                encrypted_payload.len(),
                MAX_SNAPSHOT_BYTES,
            ));
        }

        let signing_payload = Self::compute_signing_payload(
            &segment_id,
            &snapshot.ticket_id,
            &author_key_id,
            snapshot_seq,
            &snapshot.frontier_head_packs,
            snapshot.created_at,
            &encrypted_payload,
        );

        let signature = signer.sign(&signing_payload)?;

        Ok(Self {
            segment_id,
            ticket_id: snapshot.ticket_id,
            snapshot_seq,
            frontier_head_packs: snapshot.frontier_head_packs.clone(),
            created_at: snapshot.created_at,
            encrypted_payload,
            author_key_id,
            signature,
        })
    }

    /// Verifies the signature and decrypts the inner `TicketSnapshot`.
    pub fn open(
        &self,
        segment_dek: &DataEncryptionKey,
        verifier: &impl Verifier,
    ) -> Result<TicketSnapshot, SnapshotSyncError> {
        if self.encrypted_payload.len() < 28 {
            return Err(SnapshotSyncError::PayloadTooShort(
                self.encrypted_payload.len(),
            ));
        }

        // 1. Verify digital signature
        let signing_payload = Self::compute_signing_payload(
            &self.segment_id,
            &self.ticket_id,
            &self.author_key_id,
            self.snapshot_seq,
            &self.frontier_head_packs,
            self.created_at,
            &self.encrypted_payload,
        );

        verifier
            .verify(&signing_payload, &self.signature)
            .map_err(SnapshotSyncError::Signing)?;

        // 2. Decrypt AEAD payload
        let nonce = Nonce::from_slice(&self.encrypted_payload[..12]);
        let ciphertext = &self.encrypted_payload[12..];

        let aad = Self::compute_aad(
            &self.segment_id,
            &self.ticket_id,
            self.snapshot_seq,
            self.created_at,
        );

        let cipher = ChaCha20Poly1305::new(Key::from_slice(segment_dek.as_bytes()));
        let payload = Payload {
            msg: ciphertext,
            aad: &aad,
        };

        let plaintext_cbor = cipher
            .decrypt(nonce, payload)
            .map_err(|_| SnapshotSyncError::DecryptionFailed)?;

        // 3. Deserialize with strict decoder limits
        let limits = DecoderLimits {
            max_input_bytes: MAX_SNAPSHOT_BYTES,
            max_depth: 16,
            max_container_len: 65536,
            max_bytes_len: MAX_SNAPSHOT_BYTES,
        };

        let snapshot: TicketSnapshot = from_canonical_cbor(&plaintext_cbor, limits)?;

        if snapshot.ticket_id != self.ticket_id {
            return Err(SnapshotSyncError::TicketIdMismatch(
                self.ticket_id,
                snapshot.ticket_id,
            ));
        }

        Ok(snapshot)
    }
}
