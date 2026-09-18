#![allow(deprecated)]

use std::{
    fs::{self, File, OpenOptions},
    net::SocketAddr,
    path::{Path, PathBuf},
    sync::Arc,
};

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use fortiq_core::{from_canonical_cbor, Config, DecoderLimits, Genesis, NodeInfo, TicketDb};
use fortiq_p2p::{load_or_create_identity, IdentityStatus, RunOptions};
use fs2::FileExt;
use libp2p::{multiaddr::Protocol, Multiaddr};
use tracing_subscriber::EnvFilter;

mod ipc_server;
pub mod service_manager;

struct InstanceLock {
    _file: File,
}

fn instance_lock_path_for(config_path: &Path) -> PathBuf {
    let config_path = fs::canonicalize(config_path).unwrap_or_else(|_| config_path.to_path_buf());
    let config_key = blake3::hash(config_path.to_string_lossy().as_bytes());
    let instance_id = config_key.to_hex().to_string();

    #[cfg(windows)]
    {
        let base = std::env::var_os("PROGRAMDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
        base.join("FORTIQ")
            .join("instances")
            .join(format!("fortiq-service-{instance_id}.lock"))
    }

    #[cfg(not(windows))]
    {
        if rustix::process::geteuid().is_root() {
            PathBuf::from("/run/fortiq").join(format!("fortiq-service-{instance_id}.lock"))
        } else if let Some(runtime_dir) = std::env::var_os("XDG_RUNTIME_DIR") {
            PathBuf::from(runtime_dir)
                .join("fortiq")
                .join(format!("fortiq-service-{instance_id}.lock"))
        } else {
            std::env::temp_dir()
                .join("fortiq")
                .join(format!("fortiq-service-{instance_id}.lock"))
        }
    }
}

fn acquire_instance_lock_at(path: &Path) -> Result<InstanceLock> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).with_context(|| {
            format!(
                "Failed to create FORTIQ runtime directory {}",
                parent.display()
            )
        })?;
    }

    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(path)
        .with_context(|| format!("Failed to open instance lock {}", path.display()))?;
    file.try_lock_exclusive().with_context(|| {
        "Another FORTIQ service instance is already running on this operating system"
    })?;

    Ok(InstanceLock { _file: file })
}

fn acquire_instance_lock(config_path: &Path) -> Result<InstanceLock> {
    acquire_instance_lock_at(&instance_lock_path_for(config_path))
}

#[derive(Debug, Parser)]
#[command(version, about = "FORTIQ peer service")]
struct Args {
    /// Path to configuration file. If omitted, uses standard resolution.
    #[arg(long)]
    config: Option<PathBuf>,

    /// Explicit QUIC multiaddress to dial. Include /p2p/<PeerId>.
    #[arg(long)]
    dial: Option<Multiaddr>,

    /// Open an interactive shell on this peer after connecting.
    #[arg(long, requires = "dial")]
    shell: Option<libp2p::PeerId>,

    /// Run one shell command and exit. Useful for smoke tests.
    #[arg(long = "command", requires = "shell")]
    shell_command: Option<String>,

    /// Internal flag used when invoked as a background system service.
    #[arg(long = "service-run", hide = true)]
    service_run: bool,

    #[command(subcommand)]
    action: Option<Action>,
}

#[derive(Debug, Subcommand)]
enum Action {
    /// Manage support tickets.
    Ticket {
        #[command(subcommand)]
        action: TicketAction,
    },
    /// Manage the FORTIQ system service (Windows Service / systemd).
    Service {
        #[command(subcommand)]
        action: ServiceAction,
    },
}

#[derive(Debug, Subcommand)]
enum TicketAction {
    /// Show the local ticket state.
    Status,
}

#[derive(Debug, Subcommand)]
pub enum ServiceAction {
    /// Install FORTIQ as a system service.
    Install {
        /// Optional path to configuration file for the service.
        #[arg(long)]
        config: Option<PathBuf>,
    },
    /// Uninstall the FORTIQ system service.
    Uninstall,
    /// Start the FORTIQ system service.
    Start,
    /// Stop the FORTIQ system service.
    Stop,
    /// Display status of the FORTIQ system service.
    Status,
}

fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("fortiq=info")),
        )
        .with_target(false)
        .init();

    let args = Args::parse();
    let config_path = args
        .config
        .clone()
        .unwrap_or_else(Config::resolve_default_path);

    if args.service_run {
        #[cfg(windows)]
        return service_manager::windows::run_service_dispatcher(config_path);

        #[cfg(not(windows))]
        {
            let rt = tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .context("Failed to build Tokio runtime")?;
            return rt.block_on(run_daemon(config_path));
        }
    }

    if let Some(Action::Service { action }) = &args.action {
        return match action {
            ServiceAction::Install { config } => service_manager::install(config.clone()),
            ServiceAction::Uninstall => service_manager::uninstall(),
            ServiceAction::Start => service_manager::start(),
            ServiceAction::Stop => service_manager::stop(),
            ServiceAction::Status => service_manager::status(),
        };
    }

    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .context("Failed to build Tokio runtime")?;

    rt.block_on(async_main(args, config_path))
}

pub async fn run_daemon(config_path: PathBuf) -> Result<()> {
    let config = Config::load(&config_path).await?;
    let _instance_lock = acquire_instance_lock(&config_path)?;
    let ticket_db = TicketDb::try_new(config.ticket_path())
        .context("Failed to open persistent ticket database")?;
    let (keypair, _) = load_or_create_identity(&config.identity.path).await?;
    let peer_id = keypair.public().to_peer_id();
    let genesis = load_canonical_genesis(&config).await?;

    tracing::info!("FORTIQ Service starting: peer_id={peer_id}");

    let listen_address = listen_multiaddr(&config.network.listen_quic)?;
    let mut local_info = NodeInfo::local(peer_id, config.node.name.clone());
    local_info.relay = config.capabilities.relay;
    local_info.rendezvous = config.capabilities.rendezvous;

    let (p2p_cmd_tx, p2p_cmd_rx) = tokio::sync::mpsc::channel(32);

    let ipc_state = Arc::new(ipc_server::IpcState {
        config: config.clone(),
        peer_id,
        listen_addresses: vec![listen_address.to_string()],
        ticket_store: ticket_db.clone(),
        p2p_sender: Some(p2p_cmd_tx),
        operator_session: Arc::new(tokio::sync::RwLock::new(None)),
        genesis,
    });
    tokio::spawn(async move {
        if let Err(e) = ipc_server::run_ipc_server(ipc_state).await {
            tracing::warn!("Local IPC server finished with: {e}");
        }
    });

    let options = RunOptions {
        config,
        listen_address,
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        command_receiver: Some(p2p_cmd_rx),
        ticket_db,
    };
    fortiq_p2p::run(keypair, local_info, options).await
}

async fn load_canonical_genesis(config: &Config) -> Result<Option<Genesis>> {
    let path = config.genesis_path();
    let bytes = match tokio::fs::read(&path).await {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => {
            return Err(error).with_context(|| format!("Failed to read Genesis {}", path.display()))
        }
    };
    let genesis: Genesis = from_canonical_cbor(&bytes, DecoderLimits::CONTROL)
        .with_context(|| format!("Failed to decode Genesis {}", path.display()))?;
    genesis
        .verify()
        .with_context(|| format!("Failed to verify Genesis {}", path.display()))?;
    Ok(Some(genesis))
}

async fn async_main(args: Args, config_path: PathBuf) -> Result<()> {
    let config = Config::load(&config_path).await?;
    let genesis = load_canonical_genesis(&config).await?;
    let ticket_db = TicketDb::try_new(config.ticket_path())
        .context("Failed to open persistent ticket database")?;
    let dial = args.dial;

    if let Some(Action::Ticket { action }) = args.action {
        match action {
            TicketAction::Status => {
                match ticket_db.get().await? {
                    Some(ticket) => println!("Ticket {}: {}", ticket.id, ticket.state.as_str()),
                    None => println!("No ticket"),
                }
                return Ok(());
            }
        }
    }
    let _instance_lock = acquire_instance_lock(&config_path)?;
    let (keypair, identity_status) = load_or_create_identity(&config.identity.path).await?;
    let peer_id = keypair.public().to_peer_id();

    println!("FORTIQ {}\n", env!("CARGO_PKG_VERSION"));
    println!("Local PeerId:\n{peer_id}\n");
    println!(
        "Identity: {} ({})\n",
        match identity_status {
            IdentityStatus::Generated => "generated",
            IdentityStatus::Loaded => "loaded",
        },
        config.identity.path.display()
    );

    if config.capabilities.rendezvous {
        println!("Rendezvous capability: ENABLED\n");
    }
    if config.capabilities.relay {
        println!("Circuit Relay v2 capability: ENABLED\n");
    }

    let listen_address = listen_multiaddr(&config.network.listen_quic)?;
    let mut local_info = NodeInfo::local(peer_id, config.node.name.clone());
    local_info.relay = config.capabilities.relay;
    local_info.rendezvous = config.capabilities.rendezvous;

    let (p2p_cmd_tx, p2p_cmd_rx) = tokio::sync::mpsc::channel(32);

    let ipc_state = Arc::new(ipc_server::IpcState {
        config: config.clone(),
        peer_id,
        listen_addresses: vec![listen_address.to_string()],
        ticket_store: ticket_db.clone(),
        p2p_sender: Some(p2p_cmd_tx),
        operator_session: Arc::new(tokio::sync::RwLock::new(None)),
        genesis,
    });
    tokio::spawn(async move {
        if let Err(e) = ipc_server::run_ipc_server(ipc_state).await {
            tracing::warn!("Local IPC server finished with: {e}");
        }
    });

    let options = RunOptions {
        config,
        listen_address,
        dial_address: dial,
        shell_peer: args.shell,
        shell_command: args.shell_command,
        command_receiver: Some(p2p_cmd_rx),
        ticket_db,
    };
    fortiq_p2p::run(keypair, local_info, options).await
}

fn listen_multiaddr(value: &str) -> Result<Multiaddr> {
    let socket: SocketAddr = value.parse().with_context(|| {
        format!("network.listen_quic must be an IP socket address, got {value}")
    })?;
    let mut address = Multiaddr::empty();
    match socket.ip() {
        std::net::IpAddr::V4(ip) => address.push(Protocol::Ip4(ip)),
        std::net::IpAddr::V6(ip) => address.push(Protocol::Ip6(ip)),
    }
    address.push(Protocol::Udp(socket.port()));
    address.push(Protocol::QuicV1);
    Ok(address)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converts_socket_to_quic_multiaddr() {
        assert_eq!(
            listen_multiaddr("127.0.0.1:4001").unwrap().to_string(),
            "/ip4/127.0.0.1/udp/4001/quic-v1"
        );
    }

    #[test]
    fn rejects_a_second_service_instance_on_the_same_os() {
        let directory = tempfile::tempdir().unwrap();
        let lock_path = directory.path().join("fortiq-service.lock");
        let first = acquire_instance_lock_at(&lock_path).unwrap();

        let second = acquire_instance_lock_at(&lock_path);

        assert!(second.is_err());
        drop(first);
        assert!(acquire_instance_lock_at(&lock_path).is_ok());
    }

    #[test]
    fn instance_lock_path_is_accessible() {
        let p = instance_lock_path_for(Path::new("fortiq-test.toml"));
        assert!(p.to_string_lossy().contains("fortiq-service-"));
        assert!(p.to_string_lossy().ends_with(".lock"));
    }

    #[test]
    fn different_config_paths_use_different_instance_namespaces() {
        let first = instance_lock_path_for(Path::new("fortiq-first.toml"));
        let second = instance_lock_path_for(Path::new("fortiq-second.toml"));
        assert_ne!(first, second);
    }
}
