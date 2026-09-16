use std::time::Duration;

use anyhow::{Context, Result};
use fortiq_core::NodeInfo;
use futures::StreamExt;
use libp2p::{
    identify, ping,
    request_response::{self, ProtocolSupport},
    swarm::NetworkBehaviour,
    Multiaddr, PeerId, StreamProtocol, Swarm, SwarmBuilder,
};
use serde::{Deserialize, Serialize};
use tracing::{info, warn};

pub const HELLO_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/hello/1.0");
const MAX_HELLO_BYTES: usize = 16 * 1024;

#[derive(Debug, Clone, Serialize, Deserialize)]
struct HelloRequest(NodeInfo);

#[derive(Debug, Clone, Serialize, Deserialize)]
struct HelloResponse(NodeInfo);

#[derive(NetworkBehaviour)]
struct Behaviour {
    identify: identify::Behaviour,
    ping: ping::Behaviour,
    hello: request_response::json::Behaviour<HelloRequest, HelloResponse>,
}

pub async fn run(
    keypair: libp2p::identity::Keypair,
    local_info: NodeInfo,
    listen_address: Multiaddr,
    dial_address: Option<Multiaddr>,
) -> Result<()> {
    let identify_config =
        identify::Config::new("/fortiq/identify/1.0".to_owned(), keypair.public());
    let codec = request_response::json::codec::Codec::default()
        .set_request_size_maximum(MAX_HELLO_BYTES as u64)
        .set_response_size_maximum(MAX_HELLO_BYTES as u64);
    let hello = request_response::Behaviour::with_codec(
        codec,
        [(HELLO_PROTOCOL, ProtocolSupport::Full)],
        request_response::Config::default()
            .with_request_timeout(Duration::from_secs(10))
            .with_max_concurrent_streams(32),
    );
    let behaviour = Behaviour {
        identify: identify::Behaviour::new(identify_config),
        ping: ping::Behaviour::default(),
        hello,
    };

    let mut swarm = SwarmBuilder::with_existing_identity(keypair)
        .with_tokio()
        .with_quic()
        .with_behaviour(|_| behaviour)?
        .with_swarm_config(|config| config.with_idle_connection_timeout(Duration::from_secs(60)))
        .build();

    swarm
        .listen_on(listen_address)
        .context("failed to listen on QUIC address")?;

    if let Some(address) = dial_address {
        info!(%address, "dialing peer");
        swarm.dial(address).context("failed to start dial")?;
    }

    event_loop(&mut swarm, local_info).await
}

async fn event_loop(swarm: &mut Swarm<Behaviour>, local_info: NodeInfo) -> Result<()> {
    loop {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {
                info!("shutdown requested");
                return Ok(());
            }
            event = swarm.select_next_some() => match event {
                libp2p::swarm::SwarmEvent::NewListenAddr { address, .. } => {
                    println!("Listening: {address}/p2p/{}", swarm.local_peer_id());
                }
                libp2p::swarm::SwarmEvent::ConnectionEstablished { peer_id, endpoint, .. } => {
                    info!(remote_peer_id = %peer_id, ?endpoint, "authenticated connection established");
                    if endpoint.is_dialer() {
                        swarm.behaviour_mut().hello.send_request(
                            &peer_id,
                            HelloRequest(local_info.clone()),
                        );
                    }
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Hello(event)) => {
                    handle_hello(event, swarm, &local_info)?;
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Identify(
                    identify::Event::Received { peer_id, info, .. },
                )) => {
                    info!(remote_peer_id = %peer_id, protocol_version = %info.protocol_version, "identify received");
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Ping(event)) => {
                    if let Err(error) = event.result {
                        warn!(remote_peer_id = %event.peer, %error, "ping failed");
                    }
                }
                _ => {}
            }
        }
    }
}

fn handle_hello(
    event: request_response::Event<HelloRequest, HelloResponse>,
    swarm: &mut Swarm<Behaviour>,
    local_info: &NodeInfo,
) -> Result<()> {
    match event {
        request_response::Event::Message { peer, message, .. } => match message {
            request_response::Message::Request {
                request, channel, ..
            } => {
                validate_hello(&peer, &request.0)?;
                print_remote_hello(&peer, &request.0);
                swarm
                    .behaviour_mut()
                    .hello
                    .send_response(channel, HelloResponse(local_info.clone()))
                    .map_err(|_| anyhow::anyhow!("HELLO response connection closed"))?;
            }
            request_response::Message::Response { response, .. } => {
                validate_hello(&peer, &response.0)?;
                print_remote_hello(&peer, &response.0);
            }
        },
        request_response::Event::OutboundFailure { peer, error, .. } => {
            warn!(remote_peer_id = %peer, %error, "HELLO request failed");
        }
        request_response::Event::InboundFailure { peer, error, .. } => {
            warn!(remote_peer_id = %peer, %error, "HELLO response failed");
        }
        request_response::Event::ResponseSent { peer, .. } => {
            info!(remote_peer_id = %peer, "HELLO response sent");
        }
    }
    Ok(())
}

fn validate_hello(authenticated_peer: &PeerId, info: &NodeInfo) -> Result<()> {
    let serialized_size = serde_json::to_vec(info)?.len();
    if serialized_size > MAX_HELLO_BYTES {
        anyhow::bail!("HELLO metadata exceeds {MAX_HELLO_BYTES} bytes");
    }
    let claimed_peer: PeerId = info
        .peer_id
        .parse()
        .context("HELLO contains an invalid PeerId")?;
    if &claimed_peer != authenticated_peer {
        anyhow::bail!(
            "HELLO PeerId mismatch: authenticated {authenticated_peer}, claimed {claimed_peer}"
        );
    }
    Ok(())
}

fn print_remote_hello(peer: &PeerId, info: &NodeInfo) {
    println!("\nHELLO received");
    println!("remote_peer_id = {peer}");
    println!("remote_name = {}", info.name);
    println!("remote_mode = {}", info.mode);
    println!("remote_os = {}", info.os);
    println!("remote_arch = {}", info.arch);
    println!("remote_version = {}", info.version);
}
