use std::{net::SocketAddr, path::PathBuf};

use anyhow::{Context, Result};
use clap::Parser;
use fortiq_core::{Config, NodeInfo, NodeMode};
use fortiq_p2p::{load_or_create_identity, IdentityStatus};
use libp2p::{multiaddr::Protocol, Multiaddr};
use tracing_subscriber::EnvFilter;

#[derive(Debug, Parser)]
#[command(version, about = "FORTIQ peer service")]
struct Args {
    #[arg(long, default_value = "fortiq.toml")]
    config: PathBuf,

    /// Explicit QUIC multiaddress to dial. Include /p2p/<PeerId>.
    #[arg(long)]
    dial: Option<Multiaddr>,
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

    if config.capabilities.rendezvous || config.capabilities.relay {
        eprintln!("Note: rendezvous/relay capabilities are configured but not implemented before milestone 8.");
    }

    let listen_address = listen_multiaddr(&config.network.listen_quic)?;
    let local_info = NodeInfo::local(peer_id, config.node.name, mode);
    fortiq_p2p::run(keypair, local_info, listen_address, args.dial).await
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
