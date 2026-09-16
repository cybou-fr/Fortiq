use std::path::Path;

use anyhow::{Context, Result};
use libp2p::identity::Keypair;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IdentityStatus {
    Loaded,
    Generated,
}

pub async fn load_or_create_identity(path: &Path) -> Result<(Keypair, IdentityStatus)> {
    match tokio::fs::read(path).await {
        Ok(bytes) => {
            let keypair = Keypair::from_protobuf_encoding(&bytes)
                .context("identity file contains an invalid libp2p private key")?;
            Ok((keypair, IdentityStatus::Loaded))
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let keypair = Keypair::generate_ed25519();
            let encoded = keypair
                .to_protobuf_encoding()
                .context("failed to encode generated identity")?;

            if let Some(parent) = path
                .parent()
                .filter(|parent| !parent.as_os_str().is_empty())
            {
                tokio::fs::create_dir_all(parent)
                    .await
                    .with_context(|| format!("failed to create {}", parent.display()))?;
            }
            write_private_file(path, &encoded).await?;
            Ok((keypair, IdentityStatus::Generated))
        }
        Err(error) => {
            Err(error).with_context(|| format!("failed to read identity {}", path.display()))
        }
    }
}

async fn write_private_file(path: &Path, bytes: &[u8]) -> Result<()> {
    #[cfg(unix)]
    {
        let mut file = tokio::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(path)
            .await
            .with_context(|| format!("failed to create identity {}", path.display()))?;
        tokio::io::AsyncWriteExt::write_all(&mut file, bytes).await?;
        tokio::io::AsyncWriteExt::flush(&mut file).await?;
    }
    #[cfg(not(unix))]
    {
        tokio::fs::write(path, bytes)
            .await
            .with_context(|| format!("failed to create identity {}", path.display()))?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn identity_is_stable_across_loads() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("peer.identity");

        let (first, first_status) = load_or_create_identity(&path).await.unwrap();
        let (second, second_status) = load_or_create_identity(&path).await.unwrap();

        assert_eq!(first_status, IdentityStatus::Generated);
        assert_eq!(second_status, IdentityStatus::Loaded);
        assert_eq!(first.public().to_peer_id(), second.public().to_peer_id());
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn identity_has_restrictive_unix_permissions() {
        use std::os::unix::fs::PermissionsExt;
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("peer.identity");
        load_or_create_identity(&path).await.unwrap();
        let mode = std::fs::metadata(path).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o600);
    }
}
