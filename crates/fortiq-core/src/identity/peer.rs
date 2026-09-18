use anyhow::{Context, Result};
use libp2p::identity::ed25519;
use libp2p::PeerId;
use std::path::Path;

use crate::object::{Ed25519Signer, PublicKey};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IdentityStatus {
    Loaded,
    Generated,
}

/// The sovereign cryptographic identity of a FORTIQ node.
pub struct NodeIdentity {
    pub signer: Ed25519Signer,
    pub peer_id: PeerId,
}

impl NodeIdentity {
    pub fn new(signer: Ed25519Signer) -> Self {
        let pk_bytes = signer.public_key().0;
        let ed_pk = ed25519::PublicKey::try_from_bytes(&pk_bytes)
            .expect("valid 32-byte Ed25519 public key");
        let p2p_pk = libp2p::identity::PublicKey::from(ed_pk);
        let peer_id = p2p_pk.to_peer_id();

        Self { signer, peer_id }
    }

    pub fn public_key(&self) -> PublicKey {
        self.signer.public_key()
    }

    pub fn peer_id(&self) -> &PeerId {
        &self.peer_id
    }

    pub async fn load_or_create(path: &Path) -> Result<(Self, IdentityStatus)> {
        match tokio::fs::read(path).await {
            Ok(bytes) => {
                if bytes.len() < 32 {
                    anyhow::bail!("identity file is too short (must be at least 32 bytes)");
                }
                let mut seed = [0u8; 32];
                seed.copy_from_slice(&bytes[..32]);
                let signer = Ed25519Signer::from_bytes(&seed);
                Ok((Self::new(signer), IdentityStatus::Loaded))
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
                    tokio::fs::create_dir_all(parent)
                        .await
                        .with_context(|| format!("failed to create dir {}", parent.display()))?;
                }
                let mut random_bytes = [0u8; 32];
                rand_core::RngCore::fill_bytes(&mut rand_core::OsRng, &mut random_bytes);
                let generated_signer = Ed25519Signer::from_bytes(&random_bytes);
                tokio::fs::write(path, &random_bytes)
                    .await
                    .with_context(|| format!("failed to write identity {}", path.display()))?;
                Ok((Self::new(generated_signer), IdentityStatus::Generated))
            }
            Err(e) => Err(e).with_context(|| format!("failed to read identity {}", path.display())),
        }
    }
}
