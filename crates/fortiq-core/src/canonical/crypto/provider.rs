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
    #[error("invalid key length: expected 32 bytes, got {0}")]
    InvalidKeyLength(usize),
    #[error("envelope unwrapping failed")]
    EnvelopeUnwrapFailed,
    #[error("recipient key mismatch: expected {0}, got {1}")]
    RecipientKeyMismatch(KeyId, KeyId),
}

/// Generates an asymmetric KEM public/private keypair for recipient envelopes.
/// Returns (public_key_32_bytes, secret_key_32_bytes) where public_key != secret_key.
pub fn generate_kem_keypair() -> ([u8; 32], [u8; 32]) {
    let rng = OsRng;
    let sk = x25519_dalek::StaticSecret::random_from_rng(rng);
    let pk = x25519_dalek::PublicKey::from(&sk);
    (*pk.as_bytes(), sk.to_bytes())
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

    /// Wrap a DEK for a specific recipient key into a RecipientEnvelope using genuine asymmetric KEM.
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

/// Reference Cryptographic Provider implementing asymmetric KEM encapsulation
/// with X25519 Diffie-Hellman, HKDF-SHA256 key derivation, and ChaCha20Poly1305 AEAD.
#[derive(Default, Clone, Debug)]
pub struct AsymmetricKemCryptoProvider;

impl AsymmetricKemCryptoProvider {
    pub fn new() -> Self {
        Self
    }
}

pub type StandardCryptoProvider = AsymmetricKemCryptoProvider;

impl CryptoProvider for AsymmetricKemCryptoProvider {
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
        if recipient_pk.len() != 32 {
            return Err(CryptoError::InvalidKeyLength(recipient_pk.len()));
        }
        let mut pk_arr = [0u8; 32];
        pk_arr.copy_from_slice(recipient_pk);
        let rec_pk = x25519_dalek::PublicKey::from(pk_arr);

        // Genuine ephemeral KEM encapsulation:
        let ephemeral_sk = x25519_dalek::StaticSecret::random_from_rng(OsRng);
        let ephemeral_pk = x25519_dalek::PublicKey::from(&ephemeral_sk);

        // Diffie-Hellman shared secret
        let shared_secret = ephemeral_sk.diffie_hellman(&rec_pk);

        let mut kdf_context = Vec::with_capacity(ENVELOPE_WRAP_DOMAIN.len() + info.len() + 64);
        kdf_context.extend_from_slice(ENVELOPE_WRAP_DOMAIN);
        kdf_context.extend_from_slice(info);
        kdf_context.extend_from_slice(ephemeral_pk.as_bytes());
        kdf_context.extend_from_slice(rec_pk.as_bytes());

        let hk = Hkdf::<Sha256>::new(Some(rec_pk.as_bytes()), shared_secret.as_bytes());
        let mut wrapping_key = [0u8; 32];
        hk.expand(&kdf_context, &mut wrapping_key)
            .map_err(|e| CryptoError::KeyDerivationFailed(e.to_string()))?;

        // Encrypt the DEK using the derived wrapping key
        let cipher = ChaCha20Poly1305::new(Key::from_slice(&wrapping_key));
        let nonce = Nonce::from_slice(&[0u8; 12]);
        let sealed_key = cipher
            .encrypt(nonce, dek.as_bytes().as_slice())
            .map_err(|_| CryptoError::EncryptionFailed)?;

        Ok(RecipientEnvelope {
            key_id: recipient_key_id,
            key_epoch,
            hpke_enc: ephemeral_pk.as_bytes().to_vec(),
            sealed_key,
        })
    }

    fn unwrap_dek(
        &self,
        envelope: &RecipientEnvelope,
        recipient_sk: &[u8],
        info: &[u8],
    ) -> Result<DataEncryptionKey, CryptoError> {
        if recipient_sk.len() != 32 {
            return Err(CryptoError::InvalidKeyLength(recipient_sk.len()));
        }
        if envelope.hpke_enc.len() != 32 {
            return Err(CryptoError::EnvelopeUnwrapFailed);
        }

        let mut sk_arr = [0u8; 32];
        sk_arr.copy_from_slice(recipient_sk);
        let rec_sk = x25519_dalek::StaticSecret::from(sk_arr);
        let rec_pk = x25519_dalek::PublicKey::from(&rec_sk);

        let mut ephem_arr = [0u8; 32];
        ephem_arr.copy_from_slice(&envelope.hpke_enc);
        let ephemeral_pk = x25519_dalek::PublicKey::from(ephem_arr);

        // Diffie-Hellman shared secret
        let shared_secret = rec_sk.diffie_hellman(&ephemeral_pk);

        let mut kdf_context = Vec::with_capacity(ENVELOPE_WRAP_DOMAIN.len() + info.len() + 64);
        kdf_context.extend_from_slice(ENVELOPE_WRAP_DOMAIN);
        kdf_context.extend_from_slice(info);
        kdf_context.extend_from_slice(ephemeral_pk.as_bytes());
        kdf_context.extend_from_slice(rec_pk.as_bytes());

        let hk = Hkdf::<Sha256>::new(Some(rec_pk.as_bytes()), shared_secret.as_bytes());
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

/// INSECURE test-only crypto provider with pseudo-wrapping. Strictly for unit tests.
#[cfg(any(test, feature = "test-utils"))]
#[derive(Default, Clone, Debug)]
pub struct InsecureTestCryptoProvider;
