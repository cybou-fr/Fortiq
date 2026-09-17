use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::{ChaCha20Poly1305, Key, Nonce};
use hkdf::Hkdf;
use rand_core::{OsRng, RngCore};
use sha2::Sha256;
use thiserror::Error;

use crate::canonical::crypto::keys::{
    DataEncryptionKey, DerivedSegmentSecret, OwnerSegmentMasterSeed,
};
use crate::canonical::records::RecipientEnvelope;
use crate::canonical::types::{KeyId, NetworkId, SegmentId};

pub const OPERATOR_SEGMENT_KDF_SALT: &[u8] = b"FORTIQ-OPERATOR-SEGMENT-v1:";
pub const ENVELOPE_WRAP_DOMAIN: &[u8] = b"FORTIQ-ENVELOPE-WRAP-v1:";

#[derive(Error, Debug)]
pub enum CryptoError {
    #[error("AEAD encryption failed")]
    EncryptionFailed,
    #[error("AEAD decryption failed: authentication tag or AAD mismatch")]
    DecryptionFailed,
    #[error("invalid ciphertext length: {0} bytes (less than 12-byte nonce + tag)")]
    InvalidCiphertextLength(usize),
    #[error("key derivation failed: {0}")]
    KeyDerivationFailed(String),
    #[error("envelope unwrapping failed")]
    EnvelopeUnwrapFailed,
    #[error("recipient key mismatch: expected {0}, got {1}")]
    RecipientKeyMismatch(KeyId, KeyId),
}

/// Abstract Cryptographic Provider decoupling protocol serialization from backend crypto crates.
pub trait CryptoProvider: Send + Sync {
    /// Encrypt a plaintext payload with a newly generated random DEK and authenticated AAD.
    /// Returns the combined ciphertext (`nonce[12] || ciphertext || tag[16]`) and the ephemeral DEK.
    fn seal_payload(
        &self,
        plaintext: &[u8],
        aad: &[u8],
    ) -> Result<(Vec<u8>, DataEncryptionKey), CryptoError>;

    /// Decrypt a ciphertext payload using the provided DEK and authenticated AAD.
    fn open_payload(
        &self,
        ciphertext: &[u8],
        dek: &DataEncryptionKey,
        aad: &[u8],
    ) -> Result<Vec<u8>, CryptoError>;

    /// Deterministically derive an operator's segment secret key for a given client segment and epoch.
    fn derive_segment_secret(
        &self,
        master_seed: &OwnerSegmentMasterSeed,
        network_id: &NetworkId,
        segment_id: &SegmentId,
        key_epoch: u64,
    ) -> Result<DerivedSegmentSecret, CryptoError>;

    /// Wrap a DEK for a specific recipient key into a RecipientEnvelope.
    fn wrap_dek(
        &self,
        dek: &DataEncryptionKey,
        recipient_key_id: KeyId,
        recipient_pk: &[u8],
        key_epoch: u64,
        info: &[u8],
    ) -> Result<RecipientEnvelope, CryptoError>;

    /// Unwrap a DEK from a RecipientEnvelope using the recipient's private key.
    fn unwrap_dek(
        &self,
        envelope: &RecipientEnvelope,
        recipient_sk: &[u8],
        info: &[u8],
    ) -> Result<DataEncryptionKey, CryptoError>;
}

/// Standard reference CryptoProvider implementing FORTIQ-PQ1 primitives.
#[derive(Default)]
pub struct StandardCryptoProvider;

impl StandardCryptoProvider {
    pub fn new() -> Self {
        Self
    }
}

impl CryptoProvider for StandardCryptoProvider {
    fn seal_payload(
        &self,
        plaintext: &[u8],
        aad: &[u8],
    ) -> Result<(Vec<u8>, DataEncryptionKey), CryptoError> {
        let mut dek_bytes = [0u8; 32];
        OsRng.fill_bytes(&mut dek_bytes);
        let dek = DataEncryptionKey::new(dek_bytes);

        let mut nonce_bytes = [0u8; 12];
        OsRng.fill_bytes(&mut nonce_bytes);
        let nonce = Nonce::from_slice(&nonce_bytes);

        let cipher = ChaCha20Poly1305::new(Key::from_slice(dek.as_bytes()));
        let payload = Payload {
            msg: plaintext,
            aad,
        };

        let encrypted = cipher
            .encrypt(nonce, payload)
            .map_err(|_| CryptoError::EncryptionFailed)?;

        let mut result = Vec::with_capacity(12 + encrypted.len());
        result.extend_from_slice(&nonce_bytes);
        result.extend_from_slice(&encrypted);

        Ok((result, dek))
    }

    fn open_payload(
        &self,
        ciphertext: &[u8],
        dek: &DataEncryptionKey,
        aad: &[u8],
    ) -> Result<Vec<u8>, CryptoError> {
        if ciphertext.len() < 12 + 16 {
            return Err(CryptoError::InvalidCiphertextLength(ciphertext.len()));
        }

        let (nonce_bytes, encrypted) = ciphertext.split_at(12);
        let nonce = Nonce::from_slice(nonce_bytes);

        let cipher = ChaCha20Poly1305::new(Key::from_slice(dek.as_bytes()));
        let payload = Payload {
            msg: encrypted,
            aad,
        };

        cipher
            .decrypt(nonce, payload)
            .map_err(|_| CryptoError::DecryptionFailed)
    }

    fn derive_segment_secret(
        &self,
        master_seed: &OwnerSegmentMasterSeed,
        network_id: &NetworkId,
        segment_id: &SegmentId,
        key_epoch: u64,
    ) -> Result<DerivedSegmentSecret, CryptoError> {
        let hk = Hkdf::<Sha256>::new(Some(OPERATOR_SEGMENT_KDF_SALT), master_seed.as_bytes());

        let mut info = Vec::with_capacity(32 + 32 + 8);
        info.extend_from_slice(network_id.as_bytes());
        info.extend_from_slice(segment_id.as_bytes());
        info.extend_from_slice(&key_epoch.to_be_bytes());

        let mut okm = [0u8; 32];
        hk.expand(&info, &mut okm)
            .map_err(|e| CryptoError::KeyDerivationFailed(e.to_string()))?;

        Ok(DerivedSegmentSecret::new(okm))
    }

    fn wrap_dek(
        &self,
        dek: &DataEncryptionKey,
        recipient_key_id: KeyId,
        recipient_pk: &[u8],
        key_epoch: u64,
        info: &[u8],
    ) -> Result<RecipientEnvelope, CryptoError> {
        // Ephemeral KEM shared secret simulation (HKDF over ephemeral secret + recipient PK)
        let mut ephemeral_secret = [0u8; 32];
        OsRng.fill_bytes(&mut ephemeral_secret);

        let mut kdf_context = Vec::with_capacity(ENVELOPE_WRAP_DOMAIN.len() + info.len());
        kdf_context.extend_from_slice(ENVELOPE_WRAP_DOMAIN);
        kdf_context.extend_from_slice(info);

        let hk = Hkdf::<Sha256>::new(Some(recipient_pk), &ephemeral_secret);
        let mut wrapping_key = [0u8; 32];
        hk.expand(&kdf_context, &mut wrapping_key)
            .map_err(|e| CryptoError::KeyDerivationFailed(e.to_string()))?;

        // Encrypt the DEK using the derived wrapping key
        let cipher = ChaCha20Poly1305::new(Key::from_slice(&wrapping_key));
        let nonce = Nonce::from_slice(&[0u8; 12]); // Fixed nonce since wrapping key is one-time ephemeral
        let sealed_key = cipher
            .encrypt(nonce, dek.as_bytes().as_slice())
            .map_err(|_| CryptoError::EncryptionFailed)?;

        Ok(RecipientEnvelope {
            key_id: recipient_key_id,
            key_epoch,
            hpke_enc: ephemeral_secret.to_vec(),
            sealed_key,
        })
    }

    fn unwrap_dek(
        &self,
        envelope: &RecipientEnvelope,
        recipient_sk: &[u8],
        info: &[u8],
    ) -> Result<DataEncryptionKey, CryptoError> {
        let mut kdf_context = Vec::with_capacity(ENVELOPE_WRAP_DOMAIN.len() + info.len());
        kdf_context.extend_from_slice(ENVELOPE_WRAP_DOMAIN);
        kdf_context.extend_from_slice(info);

        let hk = Hkdf::<Sha256>::new(Some(recipient_sk), &envelope.hpke_enc);
        let mut wrapping_key = [0u8; 32];
        hk.expand(&kdf_context, &mut wrapping_key)
            .map_err(|e| CryptoError::KeyDerivationFailed(e.to_string()))?;

        let cipher = ChaCha20Poly1305::new(Key::from_slice(&wrapping_key));
        let nonce = Nonce::from_slice(&[0u8; 12]);
        let decrypted_dek = cipher
            .decrypt(nonce, envelope.sealed_key.as_slice())
            .map_err(|_| CryptoError::EnvelopeUnwrapFailed)?;

        if decrypted_dek.len() != 32 {
            return Err(CryptoError::EnvelopeUnwrapFailed);
        }

        let mut dek_bytes = [0u8; 32];
        dek_bytes.copy_from_slice(&decrypted_dek);
        Ok(DataEncryptionKey::new(dek_bytes))
    }
}
