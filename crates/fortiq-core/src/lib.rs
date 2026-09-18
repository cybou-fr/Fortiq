use std::{path::Path, path::PathBuf};

use anyhow::{Context, Result};
use libp2p::PeerId;
use serde::{Deserialize, Serialize};

pub mod authority;
pub mod codec;
pub mod engine;
pub mod event;
pub mod identity;
pub mod ipc;
pub mod object;
pub mod reducer;
pub mod self_support;
pub mod store;
pub mod ticket;

pub use authority::{
    derive_genesis_id, derive_owner_id, entropy_to_mnemonic, parse_mnemonic_phrase,
    AuthorityPolicy, Genesis, GenesisTbs, MemoryWorkspace, MnemonicDeriver, MnemonicEntropy,
    MnemonicError, OperatorCapabilities, OperatorSessionCertificate, OperatorSessionProof,
    OperatorSessionSeed, OwnerRootSigningSeed, OwnerSegmentMasterSeed,
};
pub use codec::{from_canonical_cbor, to_canonical_cbor, CodecError, DecoderLimits};
pub use engine::{OutboxRecord, TicketEngine};
pub type TicketDb = TicketEngine;
pub use event::{Event, EventGraph, EventPayload, Heads};
pub use identity::{IdentityStatus, NodeIdentity};
pub use object::{
    derive_signing_key_id, Ed25519Signer, Ed25519Verifier, EntityId, KeyId, NetworkId, ObjectId,
    OwnerId, PublicKey, Signature, SignedObject, Signer, SigningError, TicketId, Verifier,
};
pub use reducer::{TicketAggregate, TicketReducer, TicketStateStore};
pub use store::{FsObjectStore, ObjectStore};
pub use ticket::{
    AttachmentRecord, ChatMessage, ShellSessionRecord, TicketDetail, TicketEvent,
    TicketEventRecord, TicketPriority, TicketRecord, TicketState,
};

#[derive(Debug, Clone, Deserialize)]
pub struct Config {
    pub node: NodeConfig,
    pub identity: IdentityConfig,
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

    pub fn validate(&self) -> Result<()> {
        if self.node.name.trim().is_empty() {
            anyhow::bail!("node.name must not be empty");
        }
        if let Some(peer_id) = &self.network.bootstrap_peer {
            peer_id
                .parse::<PeerId>()
                .context("network.bootstrap_peer is not a valid libp2p PeerId")?;
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
            .unwrap_or_else(|| self.identity.path.with_file_name("ticket-store"))
    }

    /// Canonical signed Genesis stored beside the node transport identity.
    pub fn genesis_path(&self) -> PathBuf {
        self.identity.path.with_extension("genesis.cbor")
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

#[derive(Debug, Clone, Deserialize)]
pub struct NodeConfig {
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct IdentityConfig {
    pub path: PathBuf,
}

#[derive(Debug, Clone, Deserialize)]
pub struct NetworkConfig {
    #[serde(default = "default_listen_quic")]
    pub listen_quic: String,
    pub relay_peer: Option<String>,
    pub public_addr: Option<String>,
    pub bootstrap_peer: Option<String>,
}

impl Default for NetworkConfig {
    fn default() -> Self {
        Self {
            listen_quic: default_listen_quic(),
            relay_peer: None,
            public_addr: None,
            bootstrap_peer: None,
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
    #[serde(default = "default_true")]
    pub relay_rate_limit: bool,
}

impl Default for CapabilitiesConfig {
    fn default() -> Self {
        Self {
            rendezvous: false,
            relay: false,
            dcutr: true,
            relay_rate_limit: true,
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct TicketConfig {
    pub path: Option<PathBuf>,
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

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NodeInfo {
    pub peer_id: String,
    pub name: String,
    pub os: String,
    pub arch: String,
    pub version: String,
    #[serde(default)]
    pub relay: bool,
    #[serde(default)]
    pub rendezvous: bool,
}

impl NodeInfo {
    pub fn local(peer_id: PeerId, name: String) -> Self {
        Self {
            peer_id: peer_id.to_string(),
            name,
            os: std::env::consts::OS.to_owned(),
            arch: std::env::consts::ARCH.to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            relay: false,
            rendezvous: false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(bootstrap_peer: Option<String>) -> Config {
        Config {
            node: NodeConfig {
                name: "test".into(),
            },
            identity: IdentityConfig { path: "id".into() },
            network: NetworkConfig {
                bootstrap_peer,
                ..NetworkConfig::default()
            },
            capabilities: CapabilitiesConfig::default(),
            ticket: TicketConfig::default(),
            ipc: IpcConfig::default(),
        }
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
}
