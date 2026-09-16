use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};

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

struct ShellSessionGuard(Arc<AtomicBool>);

impl Drop for ShellSessionGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

#[derive(Debug)]
pub enum P2pCommand {
    ListPeers {
        reply: tokio::sync::oneshot::Sender<Vec<fortiq_core::ipc::PeerSummary>>,
    },
    CloseTicket {
        peer: PeerId,
        dial: Option<Multiaddr>,
        reply: tokio::sync::oneshot::Sender<Result<(), String>>,
    },
    OpenShellStream {
        peer: PeerId,
        dial: Option<Multiaddr>,
        reply: tokio::sync::oneshot::Sender<Result<libp2p::Stream, String>>,
    },
}

#[derive(Debug, Clone)]
pub struct DiscoveredPeer {
    pub peer_id: PeerId,
    pub name: Option<String>,
    pub os: Option<String>,
    pub arch: Option<String>,
    pub version: Option<String>,
    pub mode: Option<fortiq_core::NodeMode>,
    pub addresses: Vec<Multiaddr>,
    pub transport: String,
    pub connected: bool,
    pub last_seen: std::time::Instant,
}

#[derive(Debug, Default)]
pub struct PeerRegistry {
    peers: std::collections::HashMap<PeerId, DiscoveredPeer>,
}

impl PeerRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn record_connection(&mut self, peer_id: PeerId, endpoint: &libp2p::core::ConnectedPoint) {
        let addr = endpoint.get_remote_address();
        let is_relay = addr
            .iter()
            .any(|p| matches!(p, libp2p::multiaddr::Protocol::P2pCircuit));
        let transport = if is_relay {
            "RELAY CIRCUIT".to_string()
        } else if addr
            .iter()
            .any(|p| matches!(p, libp2p::multiaddr::Protocol::QuicV1))
        {
            "QUIC DIRECT".to_string()
        } else {
            "P2P".to_string()
        };

        let entry = self.peers.entry(peer_id).or_insert_with(|| DiscoveredPeer {
            peer_id,
            name: None,
            os: None,
            arch: None,
            version: None,
            mode: None,
            addresses: Vec::new(),
            transport: transport.clone(),
            connected: true,
            last_seen: std::time::Instant::now(),
        });
        entry.connected = true;
        entry.transport = transport;
        entry.last_seen = std::time::Instant::now();
        if !entry.addresses.contains(addr) {
            entry.addresses.push(addr.clone());
        }
    }

    pub fn record_disconnection(&mut self, peer_id: &PeerId, remaining_established: u32) {
        if remaining_established == 0 {
            if let Some(entry) = self.peers.get_mut(peer_id) {
                entry.connected = false;
                entry.last_seen = std::time::Instant::now();
            }
        }
    }

    pub fn record_hello(&mut self, peer_id: PeerId, info: &NodeInfo) {
        let entry = self.peers.entry(peer_id).or_insert_with(|| DiscoveredPeer {
            peer_id,
            name: Some(info.name.clone()),
            os: Some(info.os.clone()),
            arch: Some(info.arch.clone()),
            version: Some(info.version.clone()),
            mode: Some(info.mode),
            addresses: Vec::new(),
            transport: "P2P".to_string(),
            connected: true,
            last_seen: std::time::Instant::now(),
        });
        entry.name = Some(info.name.clone());
        entry.os = Some(info.os.clone());
        entry.arch = Some(info.arch.clone());
        entry.version = Some(info.version.clone());
        entry.mode = Some(info.mode);
        entry.last_seen = std::time::Instant::now();
    }

    pub fn record_identify(&mut self, peer_id: PeerId, info: &identify::Info) {
        let entry = self.peers.entry(peer_id).or_insert_with(|| DiscoveredPeer {
            peer_id,
            name: None,
            os: None,
            arch: None,
            version: Some(info.protocol_version.clone()),
            mode: None,
            addresses: Vec::new(),
            transport: "P2P".to_string(),
            connected: true,
            last_seen: std::time::Instant::now(),
        });
        for addr in &info.listen_addrs {
            if !entry.addresses.contains(addr) {
                entry.addresses.push(addr.clone());
            }
        }
    }

    pub fn record_rendezvous(&mut self, peer_id: PeerId, addresses: &[Multiaddr]) {
        let entry = self.peers.entry(peer_id).or_insert_with(|| DiscoveredPeer {
            peer_id,
            name: None,
            os: None,
            arch: None,
            version: None,
            mode: None,
            addresses: Vec::new(),
            transport: "P2P".to_string(),
            connected: false,
            last_seen: std::time::Instant::now(),
        });
        for addr in addresses {
            if !entry.addresses.contains(addr) {
                entry.addresses.push(addr.clone());
            }
        }
    }

    pub fn to_summaries(&self) -> Vec<fortiq_core::ipc::PeerSummary> {
        let mut list: Vec<_> = self
            .peers
            .values()
            .map(|p| {
                let hostname = p.name.clone().unwrap_or_else(|| "Inconnu".to_string());
                let os = match (&p.os, &p.arch) {
                    (Some(os), Some(arch)) => format!("{os} ({arch})"),
                    (Some(os), None) => os.clone(),
                    _ => "OS Inconnu".to_string(),
                };
                let status = if p.connected {
                    "CONNECTÉ".to_string()
                } else {
                    "DÉCOUVERT".to_string()
                };
                fortiq_core::ipc::PeerSummary {
                    peer_id: p.peer_id.to_string(),
                    hostname,
                    os,
                    transport: p.transport.clone(),
                    status,
                }
            })
            .collect();
        list.sort_by(|a, b| a.hostname.cmp(&b.hostname));
        list
    }
}

/// Builds the ordered list of addresses to dial when reaching `peer`.
///
/// Direct addresses recorded from rendezvous or identify come first, then the
/// relay circuit address, so a peer behind NAT stays reachable when every
/// direct path fails. Addresses that already carry a `/p2p/<id>` component are
/// used as-is: appending a second one produces a multiaddr libp2p rejects.
fn dial_candidates(
    peer: PeerId,
    registry: &PeerRegistry,
    relay_peer: Option<&str>,
) -> Vec<Multiaddr> {
    let mut candidates: Vec<Multiaddr> = Vec::new();

    if let Some(known) = registry.peers.get(&peer) {
        for addr in &known.addresses {
            let has_peer_id = matches!(
                addr.iter().last(),
                Some(libp2p::multiaddr::Protocol::P2p(_))
            );
            let full = if has_peer_id {
                addr.clone()
            } else {
                let mut full = addr.clone();
                full.push(libp2p::multiaddr::Protocol::P2p(peer));
                full
            };
            if !candidates.contains(&full) {
                candidates.push(full);
            }
        }
    }

    if let Some(relay) = relay_peer.and_then(|value| value.parse::<Multiaddr>().ok()) {
        let mut circuit = relay;
        circuit.push(libp2p::multiaddr::Protocol::P2pCircuit);
        circuit.push(libp2p::multiaddr::Protocol::P2p(peer));
        if !candidates.contains(&circuit) {
            candidates.push(circuit);
        }
    }

    candidates
}

/// Dials every candidate address for `peer`, logging failures instead of
/// discarding them: a silently dropped dial error used to surface only as a
/// shell stream timeout twelve seconds later.
fn dial_peer_candidates(
    swarm: &mut Swarm<Behaviour>,
    peer: PeerId,
    registry: &PeerRegistry,
    relay_peer: Option<&str>,
) {
    for address in dial_candidates(peer, registry, relay_peer) {
        match swarm.dial(address.clone()) {
            Ok(()) => info!(%address, remote_peer_id = %peer, "dialing peer"),
            Err(error) => warn!(%address, remote_peer_id = %peer, %error, "dial attempt failed"),
        }
    }
}

pub struct RunOptions {
    pub config: Config,
    pub listen_address: Multiaddr,
    pub dial_address: Option<Multiaddr>,
    pub shell_peer: Option<PeerId>,
    pub shell_command: Option<String>,
    pub close_ticket_peer: Option<PeerId>,
    pub command_receiver: Option<tokio::sync::mpsc::Receiver<P2pCommand>>,
}

struct EventOptions {
    local_info: NodeInfo,
    config: Config,
    shell_peer: Option<PeerId>,
    shell_command: Option<String>,
    close_ticket_peer: Option<PeerId>,
    ticket_store: TicketStore,
    active_shells: Arc<AtomicBool>,
    command_receiver: Option<tokio::sync::mpsc::Receiver<P2pCommand>>,
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
        command_receiver,
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

    if let Some(public_address) = config.network.public_addr.as_deref() {
        let public_address: Multiaddr = public_address
            .parse()
            .context("network.public_addr is invalid")?;
        info!(address = %public_address, "adding configured public address");
        swarm.add_external_address(public_address);
    }

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
    let active_shells = Arc::new(AtomicBool::new(false));
    let event_options = EventOptions {
        local_info,
        config,
        shell_peer,
        shell_command,
        close_ticket_peer,
        ticket_store,
        active_shells,
        command_receiver,
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
        active_shells,
        command_receiver,
    } = options;
    let (shell_result_sender, mut shell_result_receiver) = tokio::sync::mpsc::channel(1);
    let mut shell_started = false;
    let target_peer = shell_peer.or(close_ticket_peer);

    let (dummy_tx, dummy_rx) = tokio::sync::mpsc::channel(1);
    let mut command_receiver = command_receiver.unwrap_or(dummy_rx);
    let _keep_dummy_alive = dummy_tx;

    let mut peer_registry = PeerRegistry::new();
    let mut pending_close_tickets: std::collections::HashMap<
        libp2p::request_response::OutboundRequestId,
        tokio::sync::oneshot::Sender<Result<(), String>>,
    > = std::collections::HashMap::new();

    loop {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {
                info!("shutdown requested");
                return Ok(());
            }
            Some(cmd) = command_receiver.recv() => {
                match cmd {
                    P2pCommand::ListPeers { reply } => {
                        let _ = reply.send(peer_registry.to_summaries());
                    }
                    P2pCommand::CloseTicket { peer, dial, reply } => {
                        if let Some(addr) = dial {
                            if let Err(error) = swarm.dial(addr.clone()) {
                                warn!(%addr, %error, "failed to dial target for ticket close");
                            }
                        } else if !swarm.is_connected(&peer) {
                            dial_peer_candidates(
                                swarm,
                                peer,
                                &peer_registry,
                                config.network.relay_peer.as_deref(),
                            );
                        }
                        let request_id = swarm.behaviour_mut().ticket.send_request(
                            &peer,
                            TicketRequest::Close,
                        );
                        pending_close_tickets.insert(request_id, reply);
                    }
                    P2pCommand::OpenShellStream { peer, dial, reply } => {
                        if let Some(addr) = dial {
                            if let Err(error) = swarm.dial(addr.clone()) {
                                warn!(%addr, %error, "failed to dial target for shell stream");
                            }
                        } else if !swarm.is_connected(&peer) {
                            dial_peer_candidates(
                                swarm,
                                peer,
                                &peer_registry,
                                config.network.relay_peer.as_deref(),
                            );
                        }

                        let mut control = shell_control.clone();
                        tokio::spawn(async move {
                            use futures::AsyncReadExt;
                            let res = match tokio::time::timeout(
                                std::time::Duration::from_secs(12),
                                control.open_stream(peer, SHELL_PROTOCOL),
                            )
                            .await
                            {
                                Ok(Ok(mut stream)) => {
                                    let mut auth = [0u8; 1];
                                    match stream.read_exact(&mut auth).await {
                                        Ok(()) => {
                                            if auth[0] == fortiq_shell::AUTHORIZED {
                                                Ok(stream)
                                            } else {
                                                Err("L'hôte distant a refusé l'accès au terminal (ticket non ouvert ou session concurrente active)".to_string())
                                            }
                                        }
                                        Err(e) => Err(format!("Échec de lecture de l'autorisation shell: {e}")),
                                    }
                                }
                                Ok(Err(err)) => Err(format!("Échec d'ouverture du flux shell: {err}")),
                                Err(_) => Err("Délai d'attente dépassé lors de l'établissement du flux shell".to_string()),
                            };
                            let _ = reply.send(res);
                        });
                    }
                }
            }
            Some((remote_peer, stream)) = incoming_shells.next() => {
                let ticket_open = ticket_store.is_open().await.unwrap_or_else(|error| {
                    warn!(%error, "failed to check ticket state; denying shell");
                    false
                });
                let authorized = is_authorized_operator(remote_peer, &config) && ticket_open;
                let mut stream = stream;

                if !authorized {
                    warn!(remote_peer_id = %remote_peer, "denied shell from unauthorized peer or closed ticket");
                    let _ = fortiq_shell::send_authorization(&mut stream, false).await;
                    drop(stream);
                    continue;
                }

                if active_shells
                    .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
                    .is_err()
                {
                    warn!(remote_peer_id = %remote_peer, "denied shell: another shell session is already active");
                    let _ = fortiq_shell::send_authorization(&mut stream, false).await;
                    drop(stream);
                    continue;
                }

                if let Err(error) = fortiq_shell::send_authorization(&mut stream, true).await {
                    warn!(remote_peer_id = %remote_peer, %error, "failed to send shell authorization result");
                    active_shells.store(false, Ordering::SeqCst);
                    continue;
                }

                info!(remote_peer_id = %remote_peer, "accepted authorized shell");
                let info = local_info.clone();
                let shells_flag = active_shells.clone();
                tokio::spawn(async move {
                    let _guard = ShellSessionGuard(shells_flag);
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
                    peer_registry.record_connection(peer_id, &endpoint);
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
                libp2p::swarm::SwarmEvent::ConnectionClosed { peer_id, num_established, .. } => {
                    peer_registry.record_disconnection(&peer_id, num_established);
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Hello(event)) => {
                    handle_hello(event, swarm, &local_info, &mut peer_registry);
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Ticket(event)) => {
                    handle_ticket(
                        event,
                        swarm,
                        &config,
                        &ticket_store,
                        &active_shells,
                        &shell_result_sender,
                        &mut pending_close_tickets,
                    ).await;
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::RendezvousClient(event)) => {
                    handle_rendezvous_client(event, swarm, target_peer, &config, &mut peer_registry);
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
                    peer_registry.record_identify(peer_id, &info);
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

fn handle_rendezvous_client(
    event: rendezvous::client::Event,
    swarm: &mut Swarm<Behaviour>,
    target_peer: Option<PeerId>,
    config: &Config,
    peer_registry: &mut PeerRegistry,
) {
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
                if &peer_id == swarm.local_peer_id() {
                    continue;
                }
                let addresses = registration.record.addresses();
                peer_registry.record_rendezvous(peer_id, addresses);
                println!("Rendezvous discovered peer: {peer_id}");
                for address in addresses {
                    println!("  {address}/p2p/{peer_id}");
                }
                if Some(peer_id) == target_peer {
                    info!(%peer_id, "discovered requested target peer; initiating dial");
                    println!("Auto-dialing discovered target peer: {peer_id}");
                    for address in addresses {
                        let mut dial_address = address.clone();
                        dial_address.push(libp2p::multiaddr::Protocol::P2p(peer_id));
                        if let Err(error) = swarm.dial(dial_address.clone()) {
                            warn!(address = %dial_address, %error, "failed to dial discovered address");
                        }
                    }
                } else if config.mode() == fortiq_core::NodeMode::Operator
                    && !swarm.is_connected(&peer_id)
                {
                    // An operator console lists the peers it supervises, so a
                    // peer that is merely discovered is not yet usable: without
                    // a connection there is no HELLO metadata and no shell.
                    // Connecting costs nothing beyond a QUIC session and grants
                    // no authority by itself.
                    info!(%peer_id, "discovered peer; connecting to complete the inventory");
                    dial_peer_candidates(
                        swarm,
                        peer_id,
                        peer_registry,
                        config.network.relay_peer.as_deref(),
                    );
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
    active_shells: &Arc<AtomicBool>,
    completion: &tokio::sync::mpsc::Sender<Result<()>>,
    pending_close_tickets: &mut std::collections::HashMap<
        request_response::OutboundRequestId,
        tokio::sync::oneshot::Sender<Result<(), String>>,
    >,
) {
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
                } else if active_shells.load(Ordering::SeqCst) {
                    warn!(remote_peer_id = %peer, "rejected ticket close: active shell session in progress");
                    TicketResponse {
                        success: false,
                        message: "cannot close ticket while shell session is active".to_owned(),
                    }
                } else {
                    match ticket_store.close().await {
                        Ok(Some(ticket)) => {
                            info!(remote_peer_id = %peer, ticket_id = %ticket.id, "ticket closed");
                            TicketResponse {
                                success: true,
                                message: format!("ticket {} closed", ticket.id),
                            }
                        }
                        Ok(None) => TicketResponse {
                            success: false,
                            message: "no ticket exists".to_owned(),
                        },
                        Err(error) => {
                            warn!(remote_peer_id = %peer, %error, "failed to close ticket");
                            TicketResponse {
                                success: false,
                                message: "internal error closing ticket".to_owned(),
                            }
                        }
                    }
                };
                if swarm
                    .behaviour_mut()
                    .ticket
                    .send_response(channel, response)
                    .is_err()
                {
                    warn!(remote_peer_id = %peer, "ticket response connection closed before sending");
                }
            }
            request_response::Message::Response {
                request_id,
                response,
            } => {
                if let Some(reply) = pending_close_tickets.remove(&request_id) {
                    if response.success {
                        let _ = reply.send(Ok(()));
                    } else {
                        let _ = reply.send(Err(response.message.clone()));
                    }
                }
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
        request_response::Event::OutboundFailure {
            request_id,
            peer,
            error,
            ..
        } => {
            if let Some(reply) = pending_close_tickets.remove(&request_id) {
                let _ = reply.send(Err(format!("ticket request to {peer} failed: {error}")));
            }
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
}

fn handle_hello(
    event: request_response::Event<HelloRequest, HelloResponse>,
    swarm: &mut Swarm<Behaviour>,
    local_info: &NodeInfo,
    peer_registry: &mut PeerRegistry,
) {
    match event {
        request_response::Event::Message { peer, message, .. } => match message {
            request_response::Message::Request {
                request, channel, ..
            } => {
                if let Err(error) = validate_hello(&peer, &request.0) {
                    warn!(remote_peer_id = %peer, %error, "rejected invalid HELLO request");
                    return;
                }
                print_remote_hello(&peer, &request.0);
                peer_registry.record_hello(peer, &request.0);
                if swarm
                    .behaviour_mut()
                    .hello
                    .send_response(channel, HelloResponse(local_info.clone()))
                    .is_err()
                {
                    warn!(remote_peer_id = %peer, "HELLO response connection closed before sending");
                }
            }
            request_response::Message::Response { response, .. } => {
                if let Err(error) = validate_hello(&peer, &response.0) {
                    warn!(remote_peer_id = %peer, %error, "rejected invalid HELLO response");
                    return;
                }
                print_remote_hello(&peer, &response.0);
                peer_registry.record_hello(peer, &response.0);
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

#[cfg(test)]
mod tests {
    use fortiq_core::NodeMode;

    use super::*;

    #[test]
    fn validate_hello_accepts_matching_peer_id() {
        let peer_id = PeerId::random();
        let info = NodeInfo::local(peer_id, "node".to_owned(), NodeMode::Operator);
        assert!(validate_hello(&peer_id, &info).is_ok());
    }

    #[test]
    fn validate_hello_rejects_mismatched_peer_id() {
        let auth_peer = PeerId::random();
        let claimed_peer = PeerId::random();
        let info = NodeInfo::local(claimed_peer, "node".to_owned(), NodeMode::Operator);
        let error = validate_hello(&auth_peer, &info).unwrap_err();
        assert!(error.to_string().contains("HELLO PeerId mismatch"));
    }

    #[test]
    fn single_active_shell_enforced_by_atomic_and_guard() {
        let active = Arc::new(AtomicBool::new(false));

        // First session succeeds
        assert!(active
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .is_ok());

        // Second concurrent session is rejected
        assert!(active
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .is_err());

        // Guard drops and resets active flag
        {
            let _guard = ShellSessionGuard(active.clone());
            assert!(active.load(Ordering::SeqCst));
        }
        assert!(!active.load(Ordering::SeqCst));

        // New session can now be acquired
        assert!(active
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .is_ok());
    }

    #[test]
    fn dial_candidates_fall_back_to_the_relay_circuit() {
        let mut registry = PeerRegistry::new();
        let peer_id = PeerId::random();
        let relay = "/ip4/203.0.113.9/udp/4001/quic-v1/p2p/12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy";
        registry.record_rendezvous(
            peer_id,
            &["/ip4/10.0.0.5/udp/4001/quic-v1".parse().unwrap()],
        );

        let candidates = dial_candidates(peer_id, &registry, Some(relay));

        assert_eq!(candidates.len(), 2);
        assert_eq!(
            candidates[0].to_string(),
            format!("/ip4/10.0.0.5/udp/4001/quic-v1/p2p/{peer_id}")
        );
        assert_eq!(
            candidates[1].to_string(),
            format!("{relay}/p2p-circuit/p2p/{peer_id}")
        );
    }

    #[test]
    fn dial_candidates_do_not_append_a_second_peer_id() {
        let mut registry = PeerRegistry::new();
        let peer_id = PeerId::random();
        registry.record_rendezvous(
            peer_id,
            &[format!("/ip4/10.0.0.5/udp/4001/quic-v1/p2p/{peer_id}")
                .parse()
                .unwrap()],
        );

        let candidates = dial_candidates(peer_id, &registry, None);

        assert_eq!(candidates.len(), 1);
        assert_eq!(
            candidates[0].to_string(),
            format!("/ip4/10.0.0.5/udp/4001/quic-v1/p2p/{peer_id}")
        );
    }

    #[test]
    fn peer_registry_tracks_peers_and_summaries() {
        let mut registry = PeerRegistry::new();
        let peer_id = PeerId::random();
        let info = NodeInfo::local(peer_id, "OFFICE-PC".to_owned(), NodeMode::Managed);

        registry.record_hello(peer_id, &info);
        let summaries = registry.to_summaries();
        assert_eq!(summaries.len(), 1);
        assert_eq!(summaries[0].peer_id, peer_id.to_string());
        assert_eq!(summaries[0].hostname, "OFFICE-PC");
        assert_eq!(summaries[0].status, "CONNECTÉ");

        registry.record_disconnection(&peer_id, 0);
        let summaries_after = registry.to_summaries();
        assert_eq!(summaries_after[0].status, "DÉCOUVERT");
    }
}
