use std::{net::SocketAddr, path::PathBuf, sync::Arc};

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use fortiq_core::{Config, NodeInfo, NodeMode, TicketState, TicketStore};
use fortiq_p2p::{load_or_create_identity, IdentityStatus, RunOptions};
use libp2p::{multiaddr::Protocol, Multiaddr};
use tracing_subscriber::EnvFilter;

mod ipc_server;

#[derive(Debug, Parser)]
#[command(version, about = "FORTIQ peer service")]
struct Args {
    #[arg(long, default_value = "fortiq.toml")]
    config: PathBuf,

    /// Explicit QUIC multiaddress to dial. Include /p2p/<PeerId>.
    #[arg(long)]
    dial: Option<Multiaddr>,

    /// Open an interactive shell on this peer after connecting.
    #[arg(long, requires = "dial")]
    shell: Option<libp2p::PeerId>,

    /// Run one shell command and exit. Useful for smoke tests.
    #[arg(long = "command", requires = "shell")]
    shell_command: Option<String>,

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
}

#[derive(Debug, Subcommand)]
enum TicketAction {
    /// Open a local support ticket on a managed peer.
    Open,
    /// Show the local ticket state.
    Status,
    /// Close a managed peer's ticket as the operator.
    Close {
        #[arg(long)]
        peer: libp2p::PeerId,
        #[arg(long)]
        dial: Multiaddr,
    },
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("fortiq=info")),
        )
        .with_target(false)
        .init();

    let args = Args::parse();
    let config = Config::load(&args.config).await?;
    let mode = config.mode();
    let ticket_store = TicketStore::new(config.ticket_path());
    let mut dial = args.dial;
    let mut close_ticket_peer = None;

    if let Some(Action::Ticket { action }) = args.action {
        match action {
            TicketAction::Open => {
                if mode != NodeMode::Managed {
                    anyhow::bail!("Support tickets can be opened only on a managed peer.");
                }
                let ticket = ticket_store.open().await?;
                println!("Ticket {}: OPEN", ticket.id);
                return Ok(());
            }
            TicketAction::Status => {
                match ticket_store.get().await? {
                    Some(ticket) => println!(
                        "Ticket {}: {}",
                        ticket.id,
                        match ticket.state {
                            TicketState::Open => "OPEN",
                            TicketState::Closed => "CLOSED",
                        }
                    ),
                    None => println!("No ticket"),
                }
                return Ok(());
            }
            TicketAction::Close {
                peer,
                dial: close_dial,
            } => {
                if mode != NodeMode::Operator {
                    anyhow::bail!("Only the operator peer may close tickets.");
                }
                close_ticket_peer = Some(peer);
                dial = Some(close_dial);
            }
        }
    }
    if args.shell.is_some() && mode != NodeMode::Operator {
        anyhow::bail!("Administrative shell initiation is available only on the operator peer.");
    }
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
    let local_info = NodeInfo::local(peer_id, config.node.name.clone(), mode);

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
        dial_address: dial,
        shell_peer: args.shell,
        shell_command: args.shell_command,
        close_ticket_peer,
        command_receiver: Some(p2p_cmd_rx),
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
}
