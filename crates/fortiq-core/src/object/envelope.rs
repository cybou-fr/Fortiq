use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};

use super::id::ObjectId;
use super::signature::{Ed25519Signer, Ed25519Verifier, PublicKey, Signature, OBJECT_SIG_DOMAIN};

pub const OBJECT_ID_DOMAIN: &[u8] = b"FORTIQ-OBJECT-V5";

/// An immutable, cryptographically signed object in FORTIQ v5.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SignedObject {
    pub id: ObjectId,
    pub author: PublicKey,
    pub created_at: u64,
    pub payload: Vec<u8>,
    pub signature: Signature,
}

impl SignedObject {
    /// Computes the deterministic signing payload (TBS: To-Be-Signed) for an object.
    pub fn compute_tbs_bytes(author: &PublicKey, created_at: u64, payload: &[u8]) -> Vec<u8> {
        let mut tbs = Vec::with_capacity(32 + 8 + payload.len());
        tbs.extend_from_slice(author.as_bytes());
        tbs.extend_from_slice(&created_at.to_le_bytes());
        tbs.extend_from_slice(payload);
        tbs
    }

    /// Computes the content address (ObjectId) from TBS bytes.
    pub fn compute_id(author: &PublicKey, created_at: u64, payload: &[u8]) -> ObjectId {
        let tbs = Self::compute_tbs_bytes(author, created_at, payload);
        ObjectId::derive(OBJECT_ID_DOMAIN, &tbs)
    }

    /// Signs an arbitrary payload and produces a SignedObject.
    pub fn sign(signer: &Ed25519Signer, payload: Vec<u8>, created_at: u64) -> Self {
        let author = signer.public_key();
        let tbs = Self::compute_tbs_bytes(&author, created_at, &payload);
        let id = ObjectId::derive(OBJECT_ID_DOMAIN, &tbs);
        let signature = signer.sign_domain(OBJECT_SIG_DOMAIN, &tbs);

        Self {
            id,
            author,
            created_at,
            payload,
            signature,
        }
    }

    /// Verifies the cryptographic integrity and signature of this SignedObject.
    pub fn verify(&self) -> Result<()> {
        let expected_id = Self::compute_id(&self.author, self.created_at, &self.payload);
        if self.id != expected_id {
            bail!(
                "SignedObject ID mismatch: header {} does not match computed {}",
                self.id,
                expected_id
            );
        }

        let tbs = Self::compute_tbs_bytes(&self.author, self.created_at, &self.payload);
        let verifier = Ed25519Verifier::from_public_key(&self.author)
            .context("failed to construct verifier from author public key")?;
        verifier
            .verify_domain(OBJECT_SIG_DOMAIN, &tbs, &self.signature)
            .context("SignedObject signature is invalid")?;

        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_signed_object_sign_and_verify() {
        let signer = Ed25519Signer::generate();
        let payload = b"{\"action\":\"ping\"}".to_vec();
        let obj = SignedObject::sign(&signer, payload, 1_700_000_000);

        assert!(obj.verify().is_ok());

        // Tampering with payload fails
        let mut tampered = obj.clone();
        tampered.payload = b"{\"action\":\"pong\"}".to_vec();
        assert!(tampered.verify().is_err());

        // Tampering with author fails
        let another_signer = Ed25519Signer::generate();
        let mut tampered_author = obj.clone();
        tampered_author.author = another_signer.public_key();
        assert!(tampered_author.verify().is_err());
    }
}
