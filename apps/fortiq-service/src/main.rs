use std::{
    fs::{self, File, OpenOptions},
    net::SocketAddr,
    path::{Path, PathBuf},
    sync::Arc,
};

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use fortiq_core::{Config, NodeInfo, NodeMode, TicketStore};
use fortiq_p2p::{load_or_create_identity, IdentityStatus, RunOptions};
use fs2::FileExt;
use libp2p::{multiaddr::Protocol, Multiaddr};
use tracing_subscriber::EnvFilter;

mod ipc_server;
pub mod service_manager;

struct InstanceLock {
    _file: File,
}

fn instance_lock_path() -> PathBuf {
    #[cfg(windows)]
    {
        let base = std::env::var_os("PROGRAMDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
        base.join("FORTIQ").join("fortiq-service.lock")
    }

    #[cfg(not(windows))]
    {
        if rustix::process::geteuid().is_root() {
            PathBuf::from("/run/fortiq-service.lock")
        } else if let Some(runtime_dir) = std::env::var_os("XDG_RUNTIME_DIR") {
            PathBuf::from(runtime_dir)
                .join("fortiq")
                .join("fortiq-service.lock")
        } else {
            std::env::temp_dir()
                .join("fortiq")
                .join("fortiq-service.lock")
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

fn acquire_instance_lock() -> Result<InstanceLock> {
    acquire_instance_lock_at(&instance_lock_path())
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
    let _instance_lock = acquire_instance_lock()?;
    let config = Config::load(&config_path).await?;
    let mode = config.mode();
    let ticket_store = TicketStore::try_new(config.ticket_path())
        .context("Failed to open persistent ticket database")?;
    let (keypair, _) = load_or_create_identity(&config.identity.path).await?;
    let peer_id = keypair.public().to_peer_id();

    tracing::info!("FORTIQ Service starting: mode={mode}, peer_id={peer_id}");

    maybe_auto_open_ticket(&config, &ticket_store, mode, &peer_id.to_string()).await;

    let listen_address = listen_multiaddr(&config.network.listen_quic)?;
    let mut local_info = NodeInfo::local(peer_id, config.node.name.clone(), mode);
    local_info.authorized_operator = config.authorization.operator_peer_id.clone();
    local_info.relay = config.capabilities.relay;
    local_info.rendezvous = config.capabilities.rendezvous;

    let (p2p_cmd_tx, p2p_cmd_rx) = tokio::sync::mpsc::channel(32);

    let ipc_state = Arc::new(ipc_server::IpcState {
        config: config.clone(),
        peer_id,
        listen_addresses: vec![listen_address.to_string()],
        ticket_store,
        p2p_sender: Some(p2p_cmd_tx),
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
    };
    fortiq_p2p::run(keypair, local_info, options).await
}

async fn async_main(args: Args, config_path: PathBuf) -> Result<()> {
    let config = Config::load(&config_path).await?;
    let mode = config.mode();
    let ticket_store = TicketStore::try_new(config.ticket_path())
        .context("Failed to open persistent ticket database")?;
    let dial = args.dial;

    if let Some(Action::Ticket { action }) = args.action {
        match action {
            TicketAction::Status => {
                match ticket_store.get().await? {
                    Some(ticket) => println!("Ticket {}: {}", ticket.id, ticket.state.as_str()),
                    None => println!("No ticket"),
                }
                return Ok(());
            }
        }
    }
    if args.shell.is_some() && mode != NodeMode::Operator {
        anyhow::bail!("Administrative shell initiation is available only on the operator peer.");
    }
    let _instance_lock = acquire_instance_lock()?;
    let (keypair, identity_status) = load_or_create_identity(&config.identity.path).await?;
    let peer_id = keypair.public().to_peer_id();

    println!("FORTIQ {}\n", env!("CARGO_PKG_VERSION"));
    println!("MODE: {mode}\n");
    match mode {
        NodeMode::Operator => {
            println!("No operator_peer_id configured.");
            println!("This node may initiate remote administration sessions.\n");
            println!("Local operator PeerId:\n{peer_id}\n");
        }
        NodeMode::Managed => {
            println!(
                "Authorized operator:\n{}\n",
                config.authorization.operator_peer_id.as_deref().unwrap()
            );
            println!("Local PeerId:\n{peer_id}\n");
        }
    }
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
    let mut local_info = NodeInfo::local(peer_id, config.node.name.clone(), mode);
    local_info.authorized_operator = config.authorization.operator_peer_id.clone();
    local_info.relay = config.capabilities.relay;
    local_info.rendezvous = config.capabilities.rendezvous;

    let (p2p_cmd_tx, p2p_cmd_rx) = tokio::sync::mpsc::channel(32);

    let auto_open_store = ticket_store.clone();
    let ipc_state = Arc::new(ipc_server::IpcState {
        config: config.clone(),
        peer_id,
        listen_addresses: vec![listen_address.to_string()],
        ticket_store,
        p2p_sender: Some(p2p_cmd_tx),
    });
    tokio::spawn(async move {
        if let Err(e) = ipc_server::run_ipc_server(ipc_state).await {
            tracing::warn!("Local IPC server finished with: {e}");
        }
    });

    maybe_auto_open_ticket(&config, &auto_open_store, mode, &peer_id.to_string()).await;

    let options = RunOptions {
        config,
        listen_address,
        dial_address: dial,
        shell_peer: args.shell,
        shell_command: args.shell_command,
        command_receiver: Some(p2p_cmd_rx),
    };
    fortiq_p2p::run(keypair, local_info, options).await
}

/// Opens this node's ticket at start when its own config asks for it.
///
/// Lab and infrastructure nodes (our relay VPS, the WSL test peer) opt in
/// locally to staying reachable across restarts. The flag lives in this
/// machine's own config: an operator can never set it remotely, and a client
/// machine leaves it off so the ticket stays the user's consent gesture.
///
/// Both daemon entry points call this: `async_main` serves systemd, while
/// `run_daemon` serves the Windows service.
async fn maybe_auto_open_ticket(
    config: &Config,
    ticket_store: &TicketStore,
    mode: NodeMode,
    local_peer_id: &str,
) {
    if !config.ticket.auto_open {
        return;
    }
    if mode != NodeMode::Managed {
        tracing::warn!("ticket.auto_open ignored: tickets exist only on managed nodes");
        return;
    }
    if ticket_store
        .db()
        .list_tickets(None)
        .is_ok_and(|tickets| tickets.iter().any(|ticket| ticket.state.permits_work()))
    {
        return;
    }
    let Some(operator_peer_id) = config.authorization.operator_peer_id.as_deref() else {
        tracing::warn!("ticket.auto_open ignored: no authorized operator is configured");
        return;
    };
    match ticket_store.db().create_ticket(
        "Automatic support ticket",
        "Created by ticket.auto_open",
        fortiq_core::TicketPriority::Normal,
        local_peer_id,
        operator_peer_id,
    ) {
        Ok(ticket) => tracing::info!(ticket_id = %ticket.id, "ticket auto-opened at service start"),
        Err(error) => tracing::warn!(%error, "failed to auto-open ticket at start"),
    }
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
        let p = instance_lock_path();
        assert!(p.to_string_lossy().contains("fortiq-service.lock"));
    }

    #[tokio::test]
    async fn auto_open_uses_real_ticket_counterparties() {
        let directory = tempfile::tempdir().unwrap();
        let client = libp2p::PeerId::random().to_string();
        let operator = libp2p::PeerId::random().to_string();
        let config = Config {
            node: fortiq_core::NodeConfig {
                name: "managed-test".to_string(),
            },
            identity: fortiq_core::IdentityConfig {
                path: directory.path().join("identity.key"),
            },
            authorization: fortiq_core::AuthorizationConfig {
                operator_peer_id: Some(operator.clone()),
            },
            network: fortiq_core::NetworkConfig::default(),
            capabilities: fortiq_core::CapabilitiesConfig::default(),
            ticket: fortiq_core::TicketConfig {
                auto_open: true,
                ..fortiq_core::TicketConfig::default()
            },
            ipc: fortiq_core::IpcConfig::default(),
        };
        let store = TicketStore::new(directory.path().join("tickets.db"));

        maybe_auto_open_ticket(&config, &store, NodeMode::Managed, &client).await;
        maybe_auto_open_ticket(&config, &store, NodeMode::Managed, &client).await;

        let tickets = store.db().list_tickets(None).unwrap();
        assert_eq!(tickets.len(), 1);
        assert_eq!(tickets[0].client_peer_id, client);
        assert_eq!(tickets[0].operator_peer_id, operator);
        assert!(!tickets[0].remote_access_enabled);
    }
}
