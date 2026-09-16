use std::time::Duration;

use anyhow::{Context, Result};
use fortiq_core::{is_authorized_operator, Config, NodeInfo, TicketStore};
use futures::StreamExt;
use libp2p::{
    dcutr, identify, noise, ping, relay, rendezvous,
    request_response::{self, ProtocolSupport},
    swarm::{behaviour::toggle::Toggle, NetworkBehaviour},
    Multiaddr, PeerId, StreamProtocol, Swarm, SwarmBuilder,
};
use serde::{Deserialize, Serialize};
use tracing::{info, warn};

pub const HELLO_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/hello/1.0");
pub const SHELL_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/shell/1.0");
pub const TICKET_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/ticket/1.0");
const MAX_HELLO_BYTES: usize = 16 * 1024;

pub struct RunOptions {
    pub config: Config,
    pub listen_address: Multiaddr,
    pub dial_address: Option<Multiaddr>,
    pub shell_peer: Option<PeerId>,
    pub shell_command: Option<String>,
    pub close_ticket_peer: Option<PeerId>,
}

struct EventOptions {
    local_info: NodeInfo,
    config: Config,
    shell_peer: Option<PeerId>,
    shell_command: Option<String>,
    close_ticket_peer: Option<PeerId>,
    ticket_store: TicketStore,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct HelloRequest(NodeInfo);

#[derive(Debug, Clone, Serialize, Deserialize)]
struct HelloResponse(NodeInfo);

#[derive(Debug, Clone, Serialize, Deserialize)]
enum TicketRequest {
    Close,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct TicketResponse {
    success: bool,
    message: String,
}

#[derive(NetworkBehaviour)]
struct Behaviour {
    identify: identify::Behaviour,
    ping: ping::Behaviour,
    hello: request_response::json::Behaviour<HelloRequest, HelloResponse>,
    ticket: request_response::json::Behaviour<TicketRequest, TicketResponse>,
    stream: libp2p_stream::Behaviour,
    rendezvous_client: rendezvous::client::Behaviour,
    rendezvous_server: Toggle<rendezvous::server::Behaviour>,
    relay_client: relay::client::Behaviour,
    relay_server: Toggle<relay::Behaviour>,
    dcutr: dcutr::Behaviour,
}

pub async fn run(
    keypair: libp2p::identity::Keypair,
    local_info: NodeInfo,
    options: RunOptions,
) -> Result<()> {
    let RunOptions {
        config,
        listen_address,
        dial_address,
        shell_peer,
        shell_command,
        close_ticket_peer,
    } = options;
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
    let local_peer_id = keypair.public().to_peer_id();
    let rendezvous_client_key = keypair.clone();
    let rendezvous_enabled = config.capabilities.rendezvous;
    let relay_enabled = config.capabilities.relay;
    let mut swarm = SwarmBuilder::with_existing_identity(keypair)
        .with_tokio()
        .with_quic()
        .with_relay_client(noise::Config::new, libp2p::yamux::Config::default)?
        .with_behaviour(move |_, relay_client| Behaviour {
            identify: identify::Behaviour::new(identify_config),
            ping: ping::Behaviour::default(),
            hello,
            ticket: request_response::Behaviour::with_codec(
                request_response::json::codec::Codec::default()
                    .set_request_size_maximum(1024)
                    .set_response_size_maximum(4096),
                [(TICKET_PROTOCOL, ProtocolSupport::Full)],
                request_response::Config::default().with_request_timeout(Duration::from_secs(10)),
            ),
            stream: libp2p_stream::Behaviour::new(),
            rendezvous_client: rendezvous::client::Behaviour::new(rendezvous_client_key),
            rendezvous_server: rendezvous_enabled
                .then(|| rendezvous::server::Behaviour::new(Default::default()))
                .into(),
            relay_client,
            relay_server: relay_enabled
                .then(|| relay::Behaviour::new(local_peer_id, Default::default()))
                .into(),
            dcutr: dcutr::Behaviour::new(local_peer_id),
        })?
        .with_swarm_config(|config| config.with_idle_connection_timeout(Duration::from_secs(60)))
        .build();

    swarm
        .listen_on(listen_address)
        .context("failed to listen on QUIC address")?;

    if let Some(relay_address) = config.network.relay_peer.as_deref() {
        let relay_address: Multiaddr = relay_address
            .parse()
            .context("network.relay_peer is invalid")?;
        info!(address = %relay_address, "connecting to relay peer");
        let mut reservation_address = relay_address;
        reservation_address.push(libp2p::multiaddr::Protocol::P2pCircuit);
        swarm
            .listen_on(reservation_address)
            .context("failed to request relay reservation")?;
    }

    if let Some(address) = dial_address {
        info!(%address, "dialing peer");
        swarm.dial(address).context("failed to start dial")?;
    }

    let mut shell_control = swarm.behaviour().stream.new_control();
    let incoming_shells = shell_control
        .accept(SHELL_PROTOCOL)
        .context("shell protocol already registered")?;

    let ticket_store = TicketStore::new(config.ticket_path());
    let event_options = EventOptions {
        local_info,
        config,
        shell_peer,
        shell_command,
        close_ticket_peer,
        ticket_store,
    };
    event_loop(&mut swarm, incoming_shells, shell_control, event_options).await
}

async fn event_loop(
    swarm: &mut Swarm<Behaviour>,
    mut incoming_shells: libp2p_stream::IncomingStreams,
    shell_control: libp2p_stream::Control,
    options: EventOptions,
) -> Result<()> {
    let EventOptions {
        local_info,
        config,
        shell_peer,
        shell_command,
        close_ticket_peer,
        ticket_store,
    } = options;
    let (shell_result_sender, mut shell_result_receiver) = tokio::sync::mpsc::channel(1);
    let mut shell_started = false;

    loop {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {
                info!("shutdown requested");
                return Ok(());
            }
            Some((remote_peer, stream)) = incoming_shells.next() => {
                let allowed = is_authorized_operator(remote_peer, &config)
                    && ticket_store.is_open().await?;
                let mut stream = stream;
                if let Err(error) = fortiq_shell::send_authorization(&mut stream, allowed).await {
                    warn!(remote_peer_id = %remote_peer, %error, "failed to send shell authorization result");
                    continue;
                }
                if !allowed {
                    warn!(remote_peer_id = %remote_peer, "denied shell from unauthorized peer");
                    drop(stream);
                    continue;
                }

                info!(remote_peer_id = %remote_peer, "accepted authorized shell");
                let info = local_info.clone();
                tokio::spawn(async move {
                    if let Err(error) = fortiq_shell::serve(stream, info).await {
                        warn!(remote_peer_id = %remote_peer, %error, "shell session failed");
                    }
                });
            }
            Some(result) = shell_result_receiver.recv() => {
                return result;
            }
            event = swarm.select_next_some() => match event {
                libp2p::swarm::SwarmEvent::NewListenAddr { address, .. } => {
                    swarm.add_external_address(address.clone());
                    let has_local_peer_id = matches!(
                        address.iter().last(),
                        Some(libp2p::multiaddr::Protocol::P2p(peer)) if peer == *swarm.local_peer_id()
                    );
                    if has_local_peer_id {
                        println!("Listening: {address}");
                    } else {
                        println!("Listening: {address}/p2p/{}", swarm.local_peer_id());
                    }
                }
                libp2p::swarm::SwarmEvent::ConnectionEstablished { peer_id, endpoint, .. } => {
                    info!(remote_peer_id = %peer_id, ?endpoint, "authenticated connection established");
                    if endpoint.is_dialer() {
                        swarm.behaviour_mut().hello.send_request(
                            &peer_id,
                            HelloRequest(local_info.clone()),
                        );
                        if shell_peer == Some(peer_id) && !shell_started {
                            shell_started = true;
                            let mut control = shell_control.clone();
                            let sender = shell_result_sender.clone();
                            let command = shell_command.clone();
                            tokio::spawn(async move {
                                let result = async {
                                    let stream = control
                                        .open_stream(peer_id, SHELL_PROTOCOL)
                                        .await
                                        .context("failed to open remote shell stream")?;
                                    fortiq_shell::run_client(stream, command).await
                                }
                                .await;
                                let _ = sender.send(result).await;
                            });
                        }
                        if close_ticket_peer == Some(peer_id) {
                            swarm.behaviour_mut().ticket.send_request(
                                &peer_id,
                                TicketRequest::Close,
                            );
                        }
                    }
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Hello(event)) => {
                    handle_hello(event, swarm, &local_info)?;
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Ticket(event)) => {
                    handle_ticket(
                        event,
                        swarm,
                        &config,
                        &ticket_store,
                        &shell_result_sender,
                    ).await?;
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::RendezvousClient(event)) => {
                    handle_rendezvous_client(event, swarm.local_peer_id());
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::RendezvousServer(event)) => {
                    handle_rendezvous_server(event);
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::RelayClient(event)) => {
                    match event {
                        relay::client::Event::ReservationReqAccepted { relay_peer_id, renewal, .. } => {
                            info!(%relay_peer_id, renewal, "relay reservation accepted");
                        }
                        relay::client::Event::OutboundCircuitEstablished { relay_peer_id, .. } => {
                            info!(%relay_peer_id, "outbound relay circuit established");
                        }
                        relay::client::Event::InboundCircuitEstablished { src_peer_id, .. } => {
                            info!(%src_peer_id, "inbound relay circuit established");
                        }
                    }
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::RelayServer(event)) => {
                    info!(?event, "relay server event");
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Dcutr(event)) => {
                    match event.result {
                        Ok(connection_id) => {
                            info!(remote_peer_id = %event.remote_peer_id, ?connection_id, "DCUtR direct connection established");
                            println!("DCUtR direct connection established: {}", event.remote_peer_id);
                        }
                        Err(error) => {
                            warn!(remote_peer_id = %event.remote_peer_id, %error, "DCUtR upgrade failed; keeping relay connection");
                            println!("DCUtR upgrade failed; continuing through relay: {}", event.remote_peer_id);
                        }
                    }
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Identify(
                    identify::Event::Received { peer_id, info, .. },
                )) => {
                    info!(remote_peer_id = %peer_id, protocol_version = %info.protocol_version, "identify received");
                    if info
                        .protocols
                        .iter()
                        .any(|protocol| protocol.as_ref() == "/rendezvous/1.0.0")
                    {
                        let namespace = rendezvous::Namespace::from_static("fortiq");
                        if let Err(error) = swarm.behaviour_mut().rendezvous_client.register(
                            namespace.clone(),
                            peer_id,
                            None,
                        ) {
                            warn!(remote_peer_id = %peer_id, %error, "rendezvous registration could not start");
                        }
                        swarm.behaviour_mut().rendezvous_client.discover(
                            Some(namespace),
                            None,
                            None,
                            peer_id,
                        );
                    }
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Ping(event)) => {
                    if let Err(error) = event.result {
                        warn!(remote_peer_id = %event.peer, %error, "ping failed");
                    }
                }
                libp2p::swarm::SwarmEvent::ListenerError { listener_id, error } => {
                    warn!(?listener_id, %error, "listener failed");
                }
                libp2p::swarm::SwarmEvent::OutgoingConnectionError { peer_id, error, .. } => {
                    warn!(?peer_id, %error, "outgoing connection failed");
                }
                _ => {}
            }
        }
    }
}

fn handle_rendezvous_client(event: rendezvous::client::Event, local_peer_id: &PeerId) {
    match event {
        rendezvous::client::Event::Registered {
            rendezvous_node,
            namespace,
            ttl,
        } => {
            info!(%rendezvous_node, %namespace, ttl, "registered with rendezvous peer");
        }
        rendezvous::client::Event::Discovered {
            rendezvous_node,
            registrations,
            ..
        } => {
            for registration in registrations {
                let peer_id = registration.record.peer_id();
                if &peer_id == local_peer_id {
                    continue;
                }
                let addresses = registration.record.addresses();
                println!("Rendezvous discovered peer: {peer_id}");
                for address in addresses {
                    println!("  {address}/p2p/{peer_id}");
                }
            }
            info!(%rendezvous_node, "rendezvous discovery completed");
        }
        rendezvous::client::Event::RegisterFailed {
            rendezvous_node,
            error,
            ..
        } => {
            warn!(%rendezvous_node, ?error, "rendezvous registration failed");
        }
        rendezvous::client::Event::DiscoverFailed {
            rendezvous_node,
            error,
            ..
        } => {
            warn!(%rendezvous_node, ?error, "rendezvous discovery failed");
        }
        rendezvous::client::Event::Expired { peer } => {
            info!(%peer, "rendezvous registration expired");
        }
    }
}

fn handle_rendezvous_server(event: rendezvous::server::Event) {
    match event {
        rendezvous::server::Event::PeerRegistered { peer, registration } => {
            info!(%peer, namespace = %registration.namespace, "rendezvous peer registered");
        }
        rendezvous::server::Event::DiscoverServed {
            enquirer,
            registrations,
        } => {
            info!(%enquirer, count = registrations.len(), "rendezvous discovery served");
        }
        rendezvous::server::Event::PeerNotRegistered { peer, error, .. } => {
            warn!(%peer, ?error, "rendezvous registration rejected");
        }
        rendezvous::server::Event::DiscoverNotServed { enquirer, error } => {
            warn!(%enquirer, ?error, "rendezvous discovery rejected");
        }
        rendezvous::server::Event::PeerUnregistered { peer, namespace } => {
            info!(%peer, %namespace, "rendezvous peer unregistered");
        }
        rendezvous::server::Event::RegistrationExpired(registration) => {
            info!(peer = %registration.record.peer_id(), namespace = %registration.namespace, "rendezvous registration expired");
        }
    }
}

async fn handle_ticket(
    event: request_response::Event<TicketRequest, TicketResponse>,
    swarm: &mut Swarm<Behaviour>,
    config: &Config,
    ticket_store: &TicketStore,
    completion: &tokio::sync::mpsc::Sender<Result<()>>,
) -> Result<()> {
    match event {
        request_response::Event::Message { peer, message, .. } => match message {
            request_response::Message::Request {
                request: TicketRequest::Close,
                channel,
                ..
            } => {
                let response = if !is_authorized_operator(peer, config) {
                    warn!(remote_peer_id = %peer, "denied ticket close from unauthorized peer");
                    TicketResponse {
                        success: false,
                        message: "authenticated peer is not the configured operator".to_owned(),
                    }
                } else {
                    match ticket_store.close().await? {
                        Some(ticket) => {
                            info!(remote_peer_id = %peer, ticket_id = %ticket.id, "ticket closed");
                            TicketResponse {
                                success: true,
                                message: format!("ticket {} closed", ticket.id),
                            }
                        }
                        None => TicketResponse {
                            success: false,
                            message: "no ticket exists".to_owned(),
                        },
                    }
                };
                swarm
                    .behaviour_mut()
                    .ticket
                    .send_response(channel, response)
                    .map_err(|_| anyhow::anyhow!("ticket response connection closed"))?;
            }
            request_response::Message::Response { response, .. } => {
                if response.success {
                    println!("{}", response.message);
                    let _ = completion.send(Ok(())).await;
                } else {
                    let _ = completion
                        .send(Err(anyhow::anyhow!(response.message)))
                        .await;
                }
            }
        },
        request_response::Event::OutboundFailure { peer, error, .. } => {
            let _ = completion
                .send(Err(anyhow::anyhow!(
                    "ticket request to {peer} failed: {error}"
                )))
                .await;
        }
        request_response::Event::InboundFailure { peer, error, .. } => {
            warn!(remote_peer_id = %peer, %error, "ticket request failed");
        }
        request_response::Event::ResponseSent { peer, .. } => {
            info!(remote_peer_id = %peer, "ticket response sent");
        }
    }
    Ok(())
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
