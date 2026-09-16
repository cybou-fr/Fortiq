use std::{fmt, path::Path, path::PathBuf};

use anyhow::{Context, Result};
use libp2p::PeerId;
use serde::{Deserialize, Serialize};

pub mod ipc;

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
    #[serde(default)]
    pub ipc: IpcConfig,
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
        if let Some(address) = &self.network.public_addr {
            address
                .parse::<libp2p::Multiaddr>()
                .context("network.public_addr is not a valid libp2p Multiaddr")?;
        }
        Ok(())
    }

    pub fn ticket_path(&self) -> PathBuf {
        self.ticket
            .path
            .clone()
            .unwrap_or_else(|| self.identity.path.with_extension("ticket.json"))
    }

    pub fn ipc_endpoint(&self) -> String {
        #[cfg(windows)]
        {
            self.ipc.pipe.clone().unwrap_or_else(ipc::windows_pipe_name)
        }
        #[cfg(not(windows))]
        {
            self.ipc.sock.clone().unwrap_or_else(ipc::unix_socket_path)
        }
    }

    pub fn terminal_ipc_endpoint(&self) -> String {
        #[cfg(windows)]
        {
            self.ipc
                .terminal_pipe
                .clone()
                .unwrap_or_else(ipc::windows_terminal_pipe_name)
        }
        #[cfg(not(windows))]
        {
            self.ipc
                .terminal_sock
                .clone()
                .unwrap_or_else(ipc::unix_terminal_socket_path)
        }
    }

    /// Resolves the default configuration file path:
    /// 1. `FORTIQ_CONFIG` environment variable if set.
    /// 2. `./fortiq.toml` if it exists in the current working directory.
    /// 3. OS system service location:
    ///    - Windows: `%ProgramData%\FORTIQ\fortiq.toml` (if exists)
    ///    - Unix: `/etc/fortiq/fortiq.toml` (if exists)
    /// 4. Fallback: `fortiq.toml`
    pub fn resolve_default_path() -> PathBuf {
        if let Ok(env_path) = std::env::var("FORTIQ_CONFIG") {
            if !env_path.trim().is_empty() {
                return PathBuf::from(env_path);
            }
        }

        let local_path = PathBuf::from("fortiq.toml");
        if local_path.exists() {
            return local_path;
        }

        #[cfg(windows)]
        {
            if let Some(program_data) = std::env::var_os("ProgramData") {
                let system_path = PathBuf::from(program_data)
                    .join("FORTIQ")
                    .join("fortiq.toml");
                if system_path.exists() {
                    return system_path;
                }
            }
        }

        #[cfg(unix)]
        {
            let system_path = PathBuf::from("/etc/fortiq/fortiq.toml");
            if system_path.exists() {
                return system_path;
            }
        }

        local_path
    }

    /// Standard system service configuration location for service installation:
    /// - Windows: `%ProgramData%\FORTIQ\fortiq.toml`
    /// - Unix: `/etc/fortiq/fortiq.toml`
    pub fn system_service_default_path() -> PathBuf {
        #[cfg(windows)]
        {
            let base = std::env::var_os("ProgramData")
                .map(PathBuf::from)
                .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
            base.join("FORTIQ").join("fortiq.toml")
        }
        #[cfg(not(windows))]
        {
            PathBuf::from("/etc/fortiq/fortiq.toml")
        }
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
    pub public_addr: Option<String>,
}

impl Default for NetworkConfig {
    fn default() -> Self {
        Self {
            listen_quic: default_listen_quic(),
            relay_peer: None,
            public_addr: None,
        }
    }
}

fn default_listen_quic() -> String {
    "0.0.0.0:4001".to_owned()
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize)]
pub struct CapabilitiesConfig {
    #[serde(default)]
    pub rendezvous: bool,
    #[serde(default)]
    pub relay: bool,
    #[serde(default = "default_true")]
    pub dcutr: bool,
}

impl Default for CapabilitiesConfig {
    fn default() -> Self {
        Self {
            rendezvous: false,
            relay: false,
            dcutr: true,
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct TicketConfig {
    pub path: Option<PathBuf>,
    /// Opens a ticket automatically when the service starts.
    ///
    /// This is a decision the machine's own owner makes in its local config,
    /// never something a remote operator can trigger. It exists for lab and
    /// infrastructure nodes that must stay reachable across restarts; on a real
    /// client machine the ticket is the user's consent gesture and must stay
    /// off.
    #[serde(default)]
    pub auto_open: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct IpcConfig {
    pub pipe: Option<String>,
    pub terminal_pipe: Option<String>,
    pub sock: Option<String>,
    pub terminal_sock: Option<String>,
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
    #[serde(default)]
    pub authorized_operator: Option<String>,
    #[serde(default)]
    pub relay: bool,
    #[serde(default)]
    pub rendezvous: bool,
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
            authorized_operator: None,
            relay: false,
            rendezvous: false,
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
            ipc: IpcConfig::default(),
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

    #[test]
    fn ticket_auto_open_is_off_unless_the_machine_opts_in() {
        let default_config: TicketConfig = toml::from_str("").unwrap();
        assert!(!default_config.auto_open);

        let lab_node: TicketConfig = toml::from_str("auto_open = true").unwrap();
        assert!(lab_node.auto_open);
    }

    #[test]
    fn valid_public_addr_passes_validation() {
        let mut cfg = config(None);
        cfg.network.public_addr = Some("/ip4/203.0.113.1/udp/4001/quic-v1".to_owned());
        assert!(cfg.validate().is_ok());
    }

    #[test]
    fn invalid_public_addr_fails_validation() {
        let mut cfg = config(None);
        cfg.network.public_addr = Some("not-a-multiaddr".to_owned());
        assert!(cfg.validate().is_err());
    }

    #[test]
    fn resolves_config_path_from_env() {
        std::env::set_var("FORTIQ_CONFIG", "custom_path.toml");
        assert_eq!(
            Config::resolve_default_path(),
            PathBuf::from("custom_path.toml")
        );
        std::env::remove_var("FORTIQ_CONFIG");
    }
}
