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
    #[serde(default)]
    pub ticket: TicketConfig,
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
        if let Some(address) = &self.network.relay_peer {
            address
                .parse::<libp2p::Multiaddr>()
                .context("network.relay_peer is not a valid libp2p Multiaddr")?;
        }
        Ok(())
    }

    pub fn ticket_path(&self) -> PathBuf {
        self.ticket
            .path
            .clone()
            .unwrap_or_else(|| self.identity.path.with_extension("ticket.json"))
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
    pub relay_peer: Option<String>,
}

impl Default for NetworkConfig {
    fn default() -> Self {
        Self {
            listen_quic: default_listen_quic(),
            relay_peer: None,
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

#[derive(Debug, Clone, Default, Deserialize)]
pub struct TicketConfig {
    pub path: Option<PathBuf>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Ticket {
    pub id: String,
    pub state: TicketState,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum TicketState {
    Open,
    Closed,
}

#[derive(Debug, Clone)]
pub struct TicketStore {
    path: PathBuf,
}

impl TicketStore {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub async fn get(&self) -> Result<Option<Ticket>> {
        match tokio::fs::read(&self.path).await {
            Ok(bytes) => serde_json::from_slice(&bytes)
                .context("ticket file contains invalid data")
                .map(Some),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(error) => {
                Err(error).with_context(|| format!("failed to read ticket {}", self.path.display()))
            }
        }
    }

    pub async fn is_open(&self) -> Result<bool> {
        Ok(self
            .get()
            .await?
            .is_some_and(|ticket| ticket.state == TicketState::Open))
    }

    pub async fn open(&self) -> Result<Ticket> {
        if let Some(ticket) = self.get().await? {
            if ticket.state == TicketState::Open {
                return Ok(ticket);
            }
        }
        let ticket = Ticket {
            id: uuid::Uuid::new_v4().to_string(),
            state: TicketState::Open,
        };
        self.save(&ticket).await?;
        Ok(ticket)
    }

    pub async fn close(&self) -> Result<Option<Ticket>> {
        let Some(mut ticket) = self.get().await? else {
            return Ok(None);
        };
        ticket.state = TicketState::Closed;
        self.save(&ticket).await?;
        Ok(Some(ticket))
    }

    async fn save(&self, ticket: &Ticket) -> Result<()> {
        if let Some(parent) = self
            .path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            tokio::fs::create_dir_all(parent).await.with_context(|| {
                format!("failed to create ticket directory {}", parent.display())
            })?;
        }
        let bytes = serde_json::to_vec_pretty(ticket)?;
        tokio::fs::write(&self.path, bytes)
            .await
            .with_context(|| format!("failed to persist ticket {}", self.path.display()))
    }
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
            ticket: TicketConfig::default(),
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

    #[tokio::test]
    async fn ticket_lifecycle_is_persistent() {
        let directory = tempfile::tempdir().unwrap();
        let store = TicketStore::new(directory.path().join("ticket.json"));

        assert!(!store.is_open().await.unwrap());
        let opened = store.open().await.unwrap();
        assert_eq!(opened.state, TicketState::Open);
        assert!(store.is_open().await.unwrap());

        let reloaded = TicketStore::new(directory.path().join("ticket.json"));
        assert_eq!(reloaded.get().await.unwrap(), Some(opened.clone()));

        let closed = reloaded.close().await.unwrap().unwrap();
        assert_eq!(closed.id, opened.id);
        assert_eq!(closed.state, TicketState::Closed);
        assert!(!reloaded.is_open().await.unwrap());
    }
}
