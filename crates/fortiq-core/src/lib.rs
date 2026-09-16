use std::{fmt, path::Path, path::PathBuf};

use anyhow::{Context, Result};
use libp2p::PeerId;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Deserialize)]
pub struct Config {
    pub node: NodeConfig,
    pub identity: IdentityConfig,
    #[serde(default)]
    pub authorization: AuthorizationConfig,
    #[serde(default)]
    pub network: NetworkConfig,
    #[serde(default)]
    pub capabilities: CapabilitiesConfig,
}

impl Config {
    pub async fn load(path: &Path) -> Result<Self> {
        let contents = tokio::fs::read_to_string(path)
            .await
            .with_context(|| format!("failed to read config {}", path.display()))?;
        let config: Self = toml::from_str(&contents)
            .with_context(|| format!("failed to parse config {}", path.display()))?;
        config.validate()?;
        Ok(config)
    }

    pub fn mode(&self) -> NodeMode {
        if self.authorization.operator_peer_id.is_some() {
            NodeMode::Managed
        } else {
            NodeMode::Operator
        }
    }

    pub fn validate(&self) -> Result<()> {
        if self.node.name.trim().is_empty() {
            anyhow::bail!("node.name must not be empty");
        }
        if let Some(peer_id) = &self.authorization.operator_peer_id {
            peer_id
                .parse::<PeerId>()
                .context("authorization.operator_peer_id is not a valid libp2p PeerId")?;
        }
        Ok(())
    }
}

/// Returns true only when `remote_peer` is the operator explicitly configured
/// by this managed peer.
///
/// The caller must pass the authenticated PeerId supplied by libp2p, never a
/// PeerId claimed inside application payload data. Operator-mode nodes have no
/// configured remote operator and therefore return false.
pub fn is_authorized_operator(remote_peer: PeerId, config: &Config) -> bool {
    config
        .authorization
        .operator_peer_id
        .as_deref()
        .and_then(|configured| configured.parse::<PeerId>().ok())
        .is_some_and(|configured| configured == remote_peer)
}

#[derive(Debug, Clone, Deserialize)]
pub struct NodeConfig {
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct IdentityConfig {
    pub path: PathBuf,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct AuthorizationConfig {
    pub operator_peer_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct NetworkConfig {
    #[serde(default = "default_listen_quic")]
    pub listen_quic: String,
}

impl Default for NetworkConfig {
    fn default() -> Self {
        Self {
            listen_quic: default_listen_quic(),
        }
    }
}

fn default_listen_quic() -> String {
    "0.0.0.0:4001".to_owned()
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct CapabilitiesConfig {
    #[serde(default)]
    pub rendezvous: bool,
    #[serde(default)]
    pub relay: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum NodeMode {
    Operator,
    Managed,
}

impl fmt::Display for NodeMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Operator => f.write_str("OPERATOR"),
            Self::Managed => f.write_str("MANAGED"),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NodeInfo {
    pub peer_id: String,
    pub name: String,
    pub mode: NodeMode,
    pub os: String,
    pub arch: String,
    pub version: String,
}

impl NodeInfo {
    pub fn local(peer_id: PeerId, name: String, mode: NodeMode) -> Self {
        Self {
            peer_id: peer_id.to_string(),
            name,
            mode,
            os: std::env::consts::OS.to_owned(),
            arch: std::env::consts::ARCH.to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(operator_peer_id: Option<String>) -> Config {
        Config {
            node: NodeConfig {
                name: "test".into(),
            },
            identity: IdentityConfig { path: "id".into() },
            authorization: AuthorizationConfig { operator_peer_id },
            network: NetworkConfig::default(),
            capabilities: CapabilitiesConfig::default(),
        }
    }

    #[test]
    fn absent_operator_id_means_operator() {
        assert_eq!(config(None).mode(), NodeMode::Operator);
    }

    #[test]
    fn present_operator_id_means_managed() {
        let peer_id = peer_id().to_string();
        assert_eq!(config(Some(peer_id)).mode(), NodeMode::Managed);
    }

    #[test]
    fn configured_operator_is_authorized() {
        let operator = peer_id();
        let managed = config(Some(operator.to_string()));

        assert!(is_authorized_operator(operator, &managed));
    }

    #[test]
    fn different_peer_is_not_authorized() {
        let operator = peer_id();
        let other = peer_id();
        let managed = config(Some(operator.to_string()));

        assert!(!is_authorized_operator(other, &managed));
    }

    #[test]
    fn operator_mode_does_not_authorize_a_remote_operator() {
        assert!(!is_authorized_operator(peer_id(), &config(None)));
    }

    fn peer_id() -> PeerId {
        libp2p::identity::Keypair::generate_ed25519()
            .public()
            .to_peer_id()
    }
}
