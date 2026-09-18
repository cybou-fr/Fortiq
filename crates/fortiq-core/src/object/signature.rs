use anyhow::{Context, Result};
use ed25519_dalek::Signer as DalekSigner;
use rand_core::{OsRng, RngCore};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::fmt;
use std::str::FromStr;

use super::id::ObjectId;

pub const OBJECT_SIG_DOMAIN: &[u8] = b"FORTIQ-OBJECT-SIG-V5";

#[derive(Debug, thiserror::Error, Clone, PartialEq, Eq)]
#[error("signing error: {0}")]
pub struct SigningError(pub String);

/// General signing trait used across FORTIQ subsystems.
pub trait Signer {
    fn sign(&self, payload: &[u8]) -> Result<Vec<u8>, SigningError>;

    fn sign_domain(&self, domain: &[u8], payload: &[u8]) -> Result<Signature, SigningError> {
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(b":");
        hasher.update(payload);
        let digest = hasher.finalize();
        let sig = self.sign(digest.as_bytes())?;
        if sig.len() != 64 {
            return Err(SigningError("invalid signature length".to_string()));
        }
        let mut arr = [0u8; 64];
        arr.copy_from_slice(&sig);
        Ok(Signature(arr))
    }
}

impl<T: Signer + ?Sized> Signer for std::sync::Arc<T> {
    fn sign(&self, payload: &[u8]) -> Result<Vec<u8>, SigningError> {
        (**self).sign(payload)
    }

    fn sign_domain(&self, domain: &[u8], payload: &[u8]) -> Result<Signature, SigningError> {
        (**self).sign_domain(domain, payload)
    }
}

/// General verification trait used across FORTIQ subsystems.
pub trait Verifier {
    fn verify(&self, payload: &[u8], signature: &[u8]) -> Result<(), SigningError>;
}

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct PublicKey(pub [u8; 32]);

impl PublicKey {
    pub const fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub const fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }

    pub fn to_hex(&self) -> String {
        hex::encode(self.0)
    }

    pub fn from_hex(s: &str) -> Result<Self, hex::FromHexError> {
        let mut bytes = [0u8; 32];
        hex::decode_to_slice(s.trim(), &mut bytes)?;
        Ok(Self(bytes))
    }

    pub fn derive_id(&self) -> ObjectId {
        ObjectId::derive(b"FORTIQ-KEY-ID-V5", &self.0)
    }

    pub fn to_vec(&self) -> Vec<u8> {
        self.0.to_vec()
    }
}

impl std::ops::Deref for PublicKey {
    type Target = [u8; 32];
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

pub fn derive_signing_key_id(pk: &PublicKey) -> ObjectId {
    pk.derive_id()
}

impl fmt::Display for PublicKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.to_hex())
    }
}

impl fmt::Debug for PublicKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "PublicKey({})", &self.to_hex()[..8])
    }
}

impl FromStr for PublicKey {
    type Err = hex::FromHexError;
    fn from_str(s: &str) -> Result<Self, Self::Err> {
        Self::from_hex(s)
    }
}

impl Serialize for PublicKey {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        if serializer.is_human_readable() {
            serializer.serialize_str(&self.to_hex())
        } else {
            serializer.serialize_bytes(&self.0)
        }
    }
}

impl<'de> Deserialize<'de> for PublicKey {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        if deserializer.is_human_readable() {
            let s = String::deserialize(deserializer)?;
            Self::from_hex(&s).map_err(serde::de::Error::custom)
        } else {
            let bytes = <Vec<u8>>::deserialize(deserializer)?;
            if bytes.len() != 32 {
                return Err(serde::de::Error::custom("expected 32 bytes for PublicKey"));
            }
            let mut arr = [0u8; 32];
            arr.copy_from_slice(&bytes);
            Ok(PublicKey(arr))
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct Signature(pub [u8; 64]);

impl Default for Signature {
    fn default() -> Self {
        Self([0u8; 64])
    }
}

impl Signature {
    pub const fn from_bytes(bytes: [u8; 64]) -> Self {
        Self(bytes)
    }

    pub const fn as_bytes(&self) -> &[u8; 64] {
        &self.0
    }

    pub fn to_hex(&self) -> String {
        hex::encode(self.0)
    }

    pub fn from_hex(s: &str) -> Result<Self, hex::FromHexError> {
        let mut bytes = [0u8; 64];
        hex::decode_to_slice(s.trim(), &mut bytes)?;
        Ok(Self(bytes))
    }
}

impl fmt::Display for Signature {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.to_hex())
    }
}

impl fmt::Debug for Signature {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Signature({}..)", &self.to_hex()[..16])
    }
}

impl Serialize for Signature {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        if serializer.is_human_readable() {
            serializer.serialize_str(&self.to_hex())
        } else {
            serializer.serialize_bytes(&self.0)
        }
    }
}

impl<'de> Deserialize<'de> for Signature {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        if deserializer.is_human_readable() {
            let s = String::deserialize(deserializer)?;
            Self::from_hex(&s).map_err(serde::de::Error::custom)
        } else {
            let bytes = <Vec<u8>>::deserialize(deserializer)?;
            if bytes.len() != 64 {
                return Err(serde::de::Error::custom("expected 64 bytes for Signature"));
            }
            let mut arr = [0u8; 64];
            arr.copy_from_slice(&bytes);
            Ok(Signature(arr))
        }
    }
}

/// Ed25519 signer holding a private key.
pub struct Ed25519Signer {
    signing_key: ed25519_dalek::SigningKey,
    public_key: PublicKey,
}

impl Ed25519Signer {
    pub fn generate() -> Self {
        let mut bytes = [0u8; 32];
        OsRng.fill_bytes(&mut bytes);
        let signing_key = ed25519_dalek::SigningKey::from_bytes(&bytes);
        let public_key = PublicKey(signing_key.verifying_key().to_bytes());
        Self {
            signing_key,
            public_key,
        }
    }

    pub fn from_bytes(bytes: &[u8; 32]) -> Self {
        let signing_key = ed25519_dalek::SigningKey::from_bytes(bytes);
        let public_key = PublicKey(signing_key.verifying_key().to_bytes());
        Self {
            signing_key,
            public_key,
        }
    }

    pub fn from_seed(seed: [u8; 32]) -> Self {
        Self::from_bytes(&seed)
    }

    pub fn public_key(&self) -> PublicKey {
        self.public_key
    }

    pub fn key_id(&self) -> ObjectId {
        self.public_key.derive_id()
    }

    pub fn sign_domain(&self, domain: &[u8], payload: &[u8]) -> Signature {
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(b":");
        hasher.update(payload);
        let digest = hasher.finalize();

        let sig = self.signing_key.sign(digest.as_bytes());
        Signature(sig.to_bytes())
    }
}

impl fmt::Debug for Ed25519Signer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Ed25519Signer({})", self.public_key())
    }
}

impl Signer for Ed25519Signer {
    fn sign(&self, payload: &[u8]) -> Result<Vec<u8>, SigningError> {
        let sig = self.sign_domain(OBJECT_SIG_DOMAIN, payload);
        Ok(sig.0.to_vec())
    }

    fn sign_domain(&self, domain: &[u8], payload: &[u8]) -> Result<Signature, SigningError> {
        Ok(self.sign_domain(domain, payload))
    }
}

/// Ed25519 verifier holding a public key.
pub struct Ed25519Verifier {
    verifying_key: ed25519_dalek::VerifyingKey,
    public_key: PublicKey,
}

impl fmt::Debug for Ed25519Verifier {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Ed25519Verifier({})", self.public_key())
    }
}

impl Ed25519Verifier {
    pub fn from_public_key(pk: &PublicKey) -> Result<Self> {
        let verifying_key = ed25519_dalek::VerifyingKey::from_bytes(&pk.0)
            .context("invalid ed25519 public key")?;
        Ok(Self {
            verifying_key,
            public_key: *pk,
        })
    }

    pub fn public_key(&self) -> PublicKey {
        self.public_key
    }

    pub fn verify_domain(&self, domain: &[u8], payload: &[u8], signature: &Signature) -> Result<()> {
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(b":");
        hasher.update(payload);
        let digest = hasher.finalize();

        let dalek_sig = ed25519_dalek::Signature::from_bytes(&signature.0);
        self.verifying_key
            .verify_strict(digest.as_bytes(), &dalek_sig)
            .context("signature verification failed")?;
        Ok(())
    }
}

impl Verifier for Ed25519Verifier {
    fn verify(&self, payload: &[u8], signature: &[u8]) -> Result<(), SigningError> {
        if signature.len() != 64 {
            return Err(SigningError("invalid signature length".to_string()));
        }
        let mut arr = [0u8; 64];
        arr.copy_from_slice(signature);
        self.verify_domain(OBJECT_SIG_DOMAIN, payload, &Signature(arr))
            .map_err(|e| SigningError(e.to_string()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_signing_and_verification() {
        let signer = Ed25519Signer::generate();
        let pubkey = signer.public_key();
        let verifier = Ed25519Verifier::from_public_key(&pubkey).unwrap();

        let domain = b"TEST-DOMAIN";
        let message = b"sample message to sign";

        let sig = signer.sign_domain(domain, message);
        assert!(verifier.verify_domain(domain, message, &sig).is_ok());
        assert!(verifier.verify_domain(domain, b"corrupted message", &sig).is_err());
    }

    #[test]
    fn test_signer_verifier_traits() {
        let signer = Ed25519Signer::generate();
        let pubkey = signer.public_key();
        let verifier = Ed25519Verifier::from_public_key(&pubkey).unwrap();

        let message = b"trait test payload";
        let sig_bytes = Signer::sign(&signer, message).unwrap();
        assert!(Verifier::verify(&verifier, message, &sig_bytes).is_ok());
    }
}
