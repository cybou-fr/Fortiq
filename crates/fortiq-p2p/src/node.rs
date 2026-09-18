use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};

use anyhow::{Context, Result};
use fortiq_core::{
    derive_signing_key_id, from_canonical_cbor, to_canonical_cbor, Config, DecoderLimits,
    Ed25519Signer, Ed25519Verifier, EntityId, Genesis, NetworkId, NodeInfo, OperatorCapabilities,
    OperatorSessionCertificate, OwnerId, Signer, TicketDb, Verifier,
};
use futures::StreamExt;
use libp2p::{
    dcutr, identify, noise, ping, relay, rendezvous,
    request_response::{self, ProtocolSupport},
    swarm::{behaviour::toggle::Toggle, NetworkBehaviour},
    Multiaddr, PeerId, StreamProtocol, Swarm, SwarmBuilder,
};
use rand_core::{OsRng, RngCore};
use serde::{Deserialize, Serialize};
use tracing::{info, warn};

struct AuthorityContext {
    network_id: NetworkId,
    owner_id: OwnerId,
    owner_verifier: Arc<Ed25519Verifier>,
}

impl AuthorityContext {
    fn from_genesis(genesis: &Genesis) -> Result<Self> {
        let owner_verifier =
            Ed25519Verifier::from_public_key(&genesis.tbs.owner_root_signing_public_key)
                .context("invalid Genesis owner public key")?;
        Ok(Self {
            network_id: genesis.tbs.network_id,
            owner_id: genesis.tbs.owner_id,
            owner_verifier: Arc::new(owner_verifier),
        })
    }

    fn verify_certificate(
        &self,
        remote_peer: &PeerId,
        certificate: &OperatorSessionCertificate,
        required_capability: u32,
        now: u64,
    ) -> bool {
        let expected_host = EntityId::from_bytes(
            *blake3::hash(&remote_peer.to_bytes()).as_bytes(),
        );
        certificate.network_id == self.network_id
            && certificate.owner_id == self.owner_id
            && certificate.host_entity == expected_host
            && certificate.operator_key_id == derive_signing_key_id(&certificate.session_pubkey)
            && certificate.capabilities.has(required_capability)
            && certificate
                .verify(self.owner_verifier.as_ref(), now)
                .is_ok()
    }
}

pub const HELLO_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/hello/1.0");
pub const SHELL_PROTOCOL_V3: StreamProtocol = StreamProtocol::new("/fortiq/shell/3.0");
pub const TICKET_PROTOCOL_V4: StreamProtocol = StreamProtocol::new("/fortiq/ticket/4.0");
pub const CHAT_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/chat/3.0");
pub const FILE_PROTOCOL: StreamProtocol = StreamProtocol::new("/fortiq/file/3.0");
const MAX_HELLO_BYTES: usize = 16 * 1024;
/// How often a node re-queries the rendezvous points it knows.
const REDISCOVERY_INTERVAL: Duration = Duration::from_secs(10);

fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

pub const MAX_FILE_SIZE: u64 = 50 * 1024 * 1024; // 50 MB
pub const MAX_TICKET_STORAGE: u64 = 500 * 1024 * 1024; // 500 MB
pub const MAX_PARALLEL_FILES: usize = 2;
const FILE_IDLE_TIMEOUT: Duration = Duration::from_secs(30);
pub const FILE_ACCEPT: u8 = 0x01;
pub const FILE_DENIED: u8 = 0x00;
pub const FILE_DENIED_NO_TICKET: u8 = 0x02;
pub const FILE_DENIED_TICKET_CLOSED: u8 = 0x03;
pub const FILE_DENIED_TOO_LARGE: u8 = 0x04;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum TicketSyncRequest {
    GetTickets {
        authority: Option<OperatorSessionProof>,
    },
    GetTicket {
        ticket_id: String,
        authority: Option<OperatorSessionProof>,
    },
    PushTicket(Box<fortiq_core::TicketRecord>),
    UpdateStatusSigned(Box<TicketStateMutation>),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TicketStateMutation {
    pub network_id: NetworkId,
    pub ticket_id: String,
    pub expected_revision: u64,
    pub new_state: fortiq_core::TicketState,
    pub request_id: [u8; 16],
    pub operator_transport_peer_id: String,
    pub certificate: OperatorSessionCertificate,
    pub signature: Vec<u8>,
}

impl TicketStateMutation {
    pub const SIGNATURE_DOMAIN: &'static [u8] = b"FORTIQ-TICKET-STATE-v1:";

    pub fn signing_payload(&self) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(Self::SIGNATURE_DOMAIN);
        payload.extend_from_slice(self.network_id.as_bytes());
        payload.extend_from_slice(&(self.ticket_id.len() as u32).to_be_bytes());
        payload.extend_from_slice(self.ticket_id.as_bytes());
        payload.extend_from_slice(&self.expected_revision.to_be_bytes());
        payload.push(self.new_state as u8);
        payload.extend_from_slice(&self.request_id);
        payload.extend_from_slice(&(self.operator_transport_peer_id.len() as u32).to_be_bytes());
        payload.extend_from_slice(self.operator_transport_peer_id.as_bytes());
        payload
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum TicketSyncResponse {
    Tickets(Vec<fortiq_core::TicketRecord>),
    Ticket(Option<fortiq_core::TicketRecord>),
    MutationApplied(Box<fortiq_core::TicketRecord>),
    MutationRejected {
        kind: MutationRejectionKind,
        message: String,
        canonical: Option<Box<fortiq_core::TicketRecord>>,
    },
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
pub enum MutationRejectionKind {
    Permanent,
    Conflict,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessageWire {
    pub id: String,
    pub ticket_id: String,
    pub body: String,
    pub created_at: u64,
    #[serde(default)]
    pub authority: Option<OperatorSessionProof>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatAckWire {
    pub message_id: String,
    pub success: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileOfferWire {
    pub ticket_id: String,
    pub file_id: String,
    pub filename: String,
    pub file_size: u64,
    pub sha256: String,
    #[serde(default)]
    pub authority: Option<OperatorSessionProof>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OperatorSessionProof {
    pub certificate: Vec<u8>,
    pub signature: Vec<u8>,
}

impl OperatorSessionProof {
    pub const SIGNATURE_DOMAIN: &'static [u8] = b"FORTIQ-SESSION-AUTH-v1:";

    pub fn signing_payload(
        operation: &str,
        ticket_id: &str,
        request_id: &str,
        created_at: u64,
        content: &[u8],
        transport_peer_id: &str,
    ) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(Self::SIGNATURE_DOMAIN);
        for value in [
            operation.as_bytes(),
            ticket_id.as_bytes(),
            request_id.as_bytes(),
            content,
            transport_peer_id.as_bytes(),
        ] {
            payload.extend_from_slice(&(value.len() as u32).to_be_bytes());
            payload.extend_from_slice(value);
        }
        payload.extend_from_slice(&created_at.to_be_bytes());
        payload
    }

    pub fn from_certificate(
        certificate: &OperatorSessionCertificate,
        signature: Vec<u8>,
    ) -> Result<Self, String> {
        Ok(Self {
            certificate: to_canonical_cbor(certificate)
                .map_err(|error| format!("Certificate serialization failed: {error}"))?,
            signature,
        })
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct FileSendOutboxPayload {
    ticket_id: String,
    file_path: std::path::PathBuf,
}

fn is_ticket_counterparty(
    ticket: &fortiq_core::TicketRecord,
    local: &str,
    remote: &PeerId,
) -> bool {
    let remote = remote.to_string();
    ticket.client_peer_id == remote || ticket.client_peer_id == local
}

#[allow(clippy::too_many_arguments)]
fn verify_operator_session_proof(
    authority: &AuthorityContext,
    remote_peer: &PeerId,
    proof: &OperatorSessionProof,
    operation: &str,
    ticket_id: &str,
    request_id: &str,
    created_at: u64,
    content: &[u8],
    required_capability: u32,
) -> bool {
    let Ok(certificate) = from_canonical_cbor::<OperatorSessionCertificate>(
        &proof.certificate,
        DecoderLimits::CONTROL,
    ) else {
        return false;
    };
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    if !authority.verify_certificate(remote_peer, &certificate, required_capability, now) {
        return false;
    }
    let Ok(session_verifier) = Ed25519Verifier::from_public_key(&certificate.session_pubkey) else {
        return false;
    };
    session_verifier
        .verify(
            &OperatorSessionProof::signing_payload(
                operation,
                ticket_id,
                request_id,
                created_at,
                content,
                &remote_peer.to_string(),
            ),
            &proof.signature,
        )
        .is_ok()
}

struct ShellSessionGuard(Arc<AtomicBool>);

struct PendingTicketSync {
    reply: tokio::sync::oneshot::Sender<Result<TicketSyncResponse, String>>,
    outbox_id: Option<String>,
}

struct PendingShellNext {
    ticket_id: String,
    certificate: OperatorSessionCertificate,
    session_signer: Arc<Ed25519Signer>,
    reply: tokio::sync::oneshot::Sender<Result<libp2p::Stream, String>>,
}

#[derive(Debug)]
pub struct OpenShellNextCommand {
    pub peer: PeerId,
    pub ticket_id: String,
    pub certificate: OperatorSessionCertificate,
    pub session_signer: Arc<Ed25519Signer>,
    pub dial: Option<Multiaddr>,
    pub reply: tokio::sync::oneshot::Sender<Result<libp2p::Stream, String>>,
}

fn is_ticket_mutation(request: &TicketSyncRequest) -> bool {
    matches!(
        request,
        TicketSyncRequest::PushTicket(_) | TicketSyncRequest::UpdateStatusSigned(_)
    )
}

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
    OpenShellStream {
        peer: PeerId,
        ticket_id: Option<String>,
        dial: Option<Multiaddr>,
        reply: tokio::sync::oneshot::Sender<Result<libp2p::Stream, String>>,
    },
    OpenShellNext(Box<OpenShellNextCommand>),
    SyncTickets {
        peer: PeerId,
        dial: Option<Multiaddr>,
        request: TicketSyncRequest,
        reply: tokio::sync::oneshot::Sender<Result<TicketSyncResponse, String>>,
    },
    SendChatMessage {
        peer: PeerId,
        dial: Option<Multiaddr>,
        message: ChatMessageWire,
        reply: tokio::sync::oneshot::Sender<Result<ChatAckWire, String>>,
    },
    SendFile {
        peer: PeerId,
        dial: Option<Multiaddr>,
        ticket_id: String,
        file_path: std::path::PathBuf,
        certificate: Option<OperatorSessionCertificate>,
        session_signer: Option<Arc<Ed25519Signer>>,
        reply: tokio::sync::oneshot::Sender<Result<fortiq_core::AttachmentRecord, String>>,
    },
}

#[derive(Debug, Clone)]
pub struct DiscoveredPeer {
    pub peer_id: PeerId,
    pub name: Option<String>,
    pub os: Option<String>,
    pub arch: Option<String>,
    pub version: Option<String>,
    pub relay: bool,
    pub rendezvous: bool,
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
            relay: false,
            rendezvous: false,
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
            relay: info.relay,
            rendezvous: info.rendezvous,
            addresses: Vec::new(),
            transport: "P2P".to_string(),
            connected: true,
            last_seen: std::time::Instant::now(),
        });
        entry.name = Some(info.name.clone());
        entry.os = Some(info.os.clone());
        entry.arch = Some(info.arch.clone());
        entry.version = Some(info.version.clone());
        entry.relay = info.relay;
        entry.rendezvous = info.rendezvous;
        entry.last_seen = std::time::Instant::now();
    }

    pub fn record_identify(&mut self, peer_id: PeerId, info: &identify::Info) {
        let entry = self.peers.entry(peer_id).or_insert_with(|| DiscoveredPeer {
            peer_id,
            name: None,
            os: None,
            arch: None,
            version: Some(info.protocol_version.clone()),
            relay: false,
            rendezvous: false,
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
            relay: false,
            rendezvous: false,
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
                    relay: p.relay,
                    rendezvous: p.rendezvous,
                }
            })
            .collect();
        list.sort_by(|a, b| a.hostname.cmp(&b.hostname));
        list
    }
}

/// Builds the ordered list of addresses to dial when reaching `peer`.
///
/// The configured relay circuit comes first because rendezvous can retain a
/// stale public/NAT address. Direct addresses remain fallback candidates and
/// DCUtR can still upgrade an established relay connection. Addresses that
/// already carry a `/p2p/<id>` component are
/// used as-is: appending a second one produces a multiaddr libp2p rejects.
fn dial_candidates(
    peer: PeerId,
    registry: &PeerRegistry,
    relay_peer: Option<&str>,
) -> Vec<Multiaddr> {
    let mut candidates: Vec<Multiaddr> = Vec::new();

    if let Some(relay) = relay_peer.and_then(|value| value.parse::<Multiaddr>().ok()) {
        let mut circuit = relay;
        circuit.push(libp2p::multiaddr::Protocol::P2pCircuit);
        circuit.push(libp2p::multiaddr::Protocol::P2p(peer));
        candidates.push(circuit);
    }

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
) -> bool {
    use libp2p::swarm::dial_opts::{DialOpts, PeerCondition};
    let addresses = dial_candidates(peer, registry, relay_peer);
    if addresses.is_empty() {
        warn!(remote_peer_id = %peer, "no address available for peer dial");
        return false;
    }
    let opts = DialOpts::peer_id(peer)
        .addresses(addresses)
        .condition(PeerCondition::DisconnectedAndNotDialing)
        .build();
    match swarm.dial(opts) {
        Ok(()) => {
            info!(remote_peer_id = %peer, "dialing peer candidates");
            true
        }
        Err(libp2p::swarm::DialError::DialPeerConditionFalse(_)) => {
            // Discovery may refresh while the same peer is already connecting.
            true
        }
        Err(error) => {
            warn!(remote_peer_id = %peer, %error, "dial attempt failed");
            false
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn spawn_open_shell_next(
    peer: PeerId,
    local_peer: PeerId,
    ticket_id: String,
    certificate: OperatorSessionCertificate,
    session_signer: Arc<Ed25519Signer>,
    mut control: libp2p_stream::Control,
    reply: tokio::sync::oneshot::Sender<Result<libp2p::Stream, String>>,
) {
    tokio::spawn(async move {
        use futures::{AsyncReadExt, AsyncWriteExt};
        let result = async {
            let mut stream = tokio::time::timeout(
                Duration::from_secs(12),
                control.open_stream(peer, SHELL_PROTOCOL_V3),
            )
            .await
            .map_err(|_| "Délai d'attente dépassé pour /fortiq/shell/next".to_string())?
            .map_err(|error| format!("Ouverture /fortiq/shell/next impossible: {error}"))?;

            let mut challenge = [0u8; 32];
            stream
                .read_exact(&mut challenge)
                .await
                .map_err(|error| format!("Lecture du challenge shell impossible: {error}"))?;
            let handshake = fortiq_shell::ShellNextHandshake::signed(
                certificate.network_id,
                ticket_id,
                local_peer.to_string(),
                certificate,
                &challenge,
                session_signer.as_ref(),
            )
            .map_err(|error| format!("Signature du challenge shell impossible: {error}"))?;
            handshake
                .write_to_async(&mut stream)
                .await
                .map_err(|error| format!("Envoi du handshake shell impossible: {error}"))?;
            stream.flush().await.map_err(|error| error.to_string())?;
            let mut authorization = [0u8; 1];
            stream
                .read_exact(&mut authorization)
                .await
                .map_err(|error| format!("Lecture de l'autorisation shell impossible: {error}"))?;
            if authorization[0] != fortiq_shell::AUTHORIZED {
                return Err(fortiq_shell::describe_denial(authorization[0]).to_string());
            }
            Ok(stream)
        }
        .await;
        let _ = reply.send(result);
    });
}

#[allow(clippy::too_many_arguments)]
fn spawn_send_file_stream(
    peer: PeerId,
    ticket_id: String,
    file_path: std::path::PathBuf,
    sender_peer_id: String,
    mut control: libp2p_stream::Control,
    ticket_db: TicketDb,
    certificate: Option<OperatorSessionCertificate>,
    session_signer: Option<Arc<Ed25519Signer>>,
    completion: (
        Option<String>,
        tokio::sync::oneshot::Sender<Result<fortiq_core::AttachmentRecord, String>>,
    ),
) {
    tokio::spawn(async move {
        let (outbox_id, reply) = completion;
        use futures::{AsyncReadExt, AsyncWriteExt};
        use sha2::{Digest, Sha256};
        use tokio::io::AsyncReadExt as TokioAsyncReadExt;

        let res = async {
            let metadata = tokio::fs::metadata(&file_path)
                .await
                .map_err(|e| format!("Impossible de lire le fichier: {e}"))?;
            let file_size = metadata.len();
            if file_size > MAX_FILE_SIZE {
                return Err(format!(
                    "Le fichier dépasse la taille maximale (50 Mo): {file_size} octets"
                ));
            }

            let file_name = file_path
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or("file.bin")
                .to_string();

            let mut file = tokio::fs::File::open(&file_path)
                .await
                .map_err(|e| format!("Impossible d'ouvrir le fichier: {e}"))?;
            let mut hasher = Sha256::new();
            let mut buf = [0u8; 8192];
            loop {
                let n = file
                    .read(&mut buf)
                    .await
                    .map_err(|e| format!("Erreur lecture: {e}"))?;
                if n == 0 {
                    break;
                }
                hasher.update(&buf[..n]);
            }
            let sha256 = format!("{:x}", hasher.finalize());

            let file_id = uuid::Uuid::new_v4().to_string();
            let offer = FileOfferWire {
                ticket_id: ticket_id.clone(),
                file_id: file_id.clone(),
                filename: file_name.clone(),
                file_size,
                sha256: sha256.clone(),
                authority: match (certificate, session_signer) {
                    (Some(certificate), Some(signer)) => {
                        let content = format!("{}:{}:{}", file_name, file_size, sha256);
                        let signature = signer
                            .sign(&OperatorSessionProof::signing_payload(
                                "file",
                                &ticket_id,
                                &file_id,
                                0,
                                content.as_bytes(),
                                &sender_peer_id,
                            ))
                            .map_err(|error| format!("Signature fichier impossible: {error}"))?;
                        Some(
                            OperatorSessionProof::from_certificate(&certificate, signature)
                                .map_err(|error| {
                                    format!("Certificate serialization failed: {error}")
                                })?,
                        )
                    }
                    _ => None,
                },
            };

            let mut stream = control
                .open_stream(peer, FILE_PROTOCOL)
                .await
                .map_err(|e| format!("Échec d'ouverture du flux fichier: {e}"))?;

            let json =
                serde_json::to_vec(&offer).map_err(|e| format!("Erreur sérialisation: {e}"))?;
            let len = u16::try_from(json.len())
                .map_err(|_| "Header de fichier trop grand".to_string())?;
            stream
                .write_all(&len.to_be_bytes())
                .await
                .map_err(|e| format!("Erreur envoi header: {e}"))?;
            stream
                .write_all(&json)
                .await
                .map_err(|e| format!("Erreur envoi offre: {e}"))?;
            stream
                .flush()
                .await
                .map_err(|e| format!("Erreur flush: {e}"))?;

            let mut response = [0u8; 1];
            stream
                .read_exact(&mut response)
                .await
                .map_err(|e| format!("Erreur lecture réponse: {e}"))?;
            if response[0] != FILE_ACCEPT {
                let reason = match response[0] {
                    FILE_DENIED_NO_TICKET => "Aucun ticket correspondant sur le poste distant",
                    FILE_DENIED_TICKET_CLOSED => "Le ticket est fermé",
                    FILE_DENIED_TOO_LARGE => "Fichier trop volumineux",
                    _ => "Transfert refusé par le poste distant",
                };
                return Err(reason.to_string());
            }

            let mut file = tokio::fs::File::open(&file_path)
                .await
                .map_err(|e| format!("Impossible de réouvrir le fichier: {e}"))?;
            let mut remaining = file_size;
            while remaining > 0 {
                let to_read = std::cmp::min(remaining, buf.len() as u64) as usize;
                let n = file
                    .read_exact(&mut buf[..to_read])
                    .await
                    .map_err(|e| format!("Erreur lecture chunk: {e}"))?;
                stream
                    .write_all(&buf[..n])
                    .await
                    .map_err(|e| format!("Erreur envoi chunk: {e}"))?;
                remaining -= n as u64;
            }
            stream
                .flush()
                .await
                .map_err(|e| format!("Erreur flush données: {e}"))?;

            let mut ack = [0u8; 1];
            stream
                .read_exact(&mut ack)
                .await
                .map_err(|e| format!("Erreur confirmation: {e}"))?;
            if ack[0] != FILE_ACCEPT {
                return Err(
                    "Échec de vérification du fichier par le destinataire (SHA-256 invalide)"
                        .to_string(),
                );
            }

            let attachment = fortiq_core::AttachmentRecord {
                id: file_id,
                ticket_id: ticket_id.clone(),
                sender_peer_id: sender_peer_id.clone(),
                filename: file_name,
                size_bytes: file_size,
                sha256,
                local_path: file_path.to_string_lossy().to_string(),
                created_at: std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_secs(),
                state: "STORED".to_string(),
            };

            ticket_db
                .add_attachment(&attachment)
                .map_err(|e| format!("Échec de persistance de la pièce jointe locale: {e}"))?;
            let _ = ticket_db.record_event(
                &ticket_id,
                "ATTACHMENT_SENT",
                &sender_peer_id,
                Some(&format!("Fichier envoyé: {}", attachment.filename)),
            );

            Ok(attachment)
        }
        .await;

        if res.is_ok() {
            if let Some(outbox_id) = outbox_id.as_deref() {
                let _ = ticket_db.remove_outbox(outbox_id);
            }
        }
        let _ = reply.send(res);
    });
}

pub struct RunOptions {
    pub config: Config,
    pub listen_address: Multiaddr,
    pub dial_address: Option<Multiaddr>,
    pub shell_peer: Option<PeerId>,
    pub shell_command: Option<String>,
    pub command_receiver: Option<tokio::sync::mpsc::Receiver<P2pCommand>>,
    pub ticket_db: TicketDb,
}

struct EventOptions {
    local_info: NodeInfo,
    config: Config,
    authority: Option<Arc<AuthorityContext>>,
    ticket_db: TicketDb,
    active_shells: Arc<AtomicBool>,
    command_receiver: Option<tokio::sync::mpsc::Receiver<P2pCommand>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct HelloRequest(NodeInfo);

#[derive(Debug, Clone, Serialize, Deserialize)]
struct HelloResponse(NodeInfo);

#[derive(NetworkBehaviour)]
struct Behaviour {
    identify: identify::Behaviour,
    ping: ping::Behaviour,
    hello: request_response::json::Behaviour<HelloRequest, HelloResponse>,
    ticket_v2: request_response::json::Behaviour<TicketSyncRequest, TicketSyncResponse>,
    chat: request_response::json::Behaviour<ChatMessageWire, ChatAckWire>,
    stream: libp2p_stream::Behaviour,
    rendezvous_client: rendezvous::client::Behaviour,
    rendezvous_server: Toggle<rendezvous::server::Behaviour>,
    relay_client: relay::client::Behaviour,
    relay_server: Toggle<relay::Behaviour>,
    dcutr: Toggle<dcutr::Behaviour>,
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
        shell_command: _shell_command,
        command_receiver,
        ticket_db,
    } = options;
    if shell_peer.is_some() {
        anyhow::bail!(
            "legacy shell/1.0 is disabled; open a ticket-scoped shell through the service IPC"
        );
    }
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
    let dcutr_enabled = config.capabilities.dcutr;
    let relay_rate_limit_enabled = config.capabilities.relay_rate_limit;
    let mut swarm = SwarmBuilder::with_existing_identity(keypair)
        .with_tokio()
        .with_quic()
        .with_relay_client(noise::Config::new, libp2p::yamux::Config::default)?
        .with_behaviour(move |_, relay_client| {
            let ticket_v2 = request_response::Behaviour::with_codec(
                request_response::json::codec::Codec::default()
                    .set_request_size_maximum(64 * 1024)
                    .set_response_size_maximum(256 * 1024),
                [(TICKET_PROTOCOL_V4, ProtocolSupport::Full)],
                request_response::Config::default().with_request_timeout(Duration::from_secs(10)),
            );
            let chat = request_response::Behaviour::with_codec(
                request_response::json::codec::Codec::default()
                    .set_request_size_maximum(64 * 1024)
                    .set_response_size_maximum(4096),
                [(CHAT_PROTOCOL, ProtocolSupport::Full)],
                request_response::Config::default().with_request_timeout(Duration::from_secs(10)),
            );
            Behaviour {
                identify: identify::Behaviour::new(identify_config),
                ping: ping::Behaviour::default(),
                hello,
                ticket_v2,
                chat,
                stream: libp2p_stream::Behaviour::new(),
                rendezvous_client: rendezvous::client::Behaviour::new(rendezvous_client_key),
                rendezvous_server: rendezvous_enabled
                    .then(|| rendezvous::server::Behaviour::new(Default::default()))
                    .into(),
                relay_client,
                relay_server: relay_enabled
                    .then(|| {
                        let mut relay_config = relay::Config {
                            max_reservations: 256,
                            max_reservations_per_peer: 16,
                            reservation_duration: Duration::from_secs(3600),
                            reservation_rate_limiters: Vec::new(),
                            max_circuits: 256,
                            max_circuits_per_peer: 16,
                            max_circuit_duration: Duration::from_secs(2 * 3600),
                            max_circuit_bytes: 1024 * 1024 * 1024,
                            circuit_src_rate_limiters: Vec::new(),
                        };
                        if relay_rate_limit_enabled {
                            let limit_peer = std::num::NonZeroU32::new(60).expect("60 > 0");
                            let limit_ip = std::num::NonZeroU32::new(120).expect("120 > 0");
                            relay_config = relay_config
                                .reservation_rate_per_peer(limit_peer, Duration::from_secs(10))
                                .reservation_rate_per_ip(limit_ip, Duration::from_secs(5))
                                .circuit_src_per_peer(limit_peer, Duration::from_secs(2))
                                .circuit_src_per_ip(limit_ip, Duration::from_secs(1));
                        }
                        relay::Behaviour::new(local_peer_id, relay_config)
                    })
                    .into(),
                dcutr: dcutr_enabled
                    .then(|| dcutr::Behaviour::new(local_peer_id))
                    .into(),
            }
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

    let mut stream_control = swarm.behaviour().stream.new_control();
    let incoming_shells_next = stream_control
        .accept(SHELL_PROTOCOL_V3)
        .context("shell next protocol already registered")?;
    let incoming_files = stream_control
        .accept(FILE_PROTOCOL)
        .context("file protocol already registered")?;

    let active_shells = Arc::new(AtomicBool::new(false));
    let authority = match tokio::fs::read(config.genesis_path()).await {
        Ok(bytes) => from_canonical_cbor::<Genesis>(&bytes, DecoderLimits::CONTROL)
            .ok()
            .filter(|genesis| genesis.verify().is_ok())
            .and_then(|genesis| AuthorityContext::from_genesis(&genesis).ok())
            .map(Arc::new),
        Err(_) => None,
    };
    let event_options = EventOptions {
        local_info,
        config,
        authority,
        ticket_db,
        active_shells,
        command_receiver,
    };
    event_loop(
        &mut swarm,
        incoming_shells_next,
        incoming_files,
        stream_control,
        event_options,
    )
    .await
}

async fn event_loop(
    swarm: &mut Swarm<Behaviour>,
    mut incoming_shells_next: libp2p_stream::IncomingStreams,
    mut incoming_files: libp2p_stream::IncomingStreams,
    stream_control: libp2p_stream::Control,
    options: EventOptions,
) -> Result<()> {
    let EventOptions {
        local_info,
        config,
        authority,
        ticket_db: ticket_store,
        active_shells,
        command_receiver,
    } = options;
    let target_peer = None;

    let (dummy_tx, dummy_rx) = tokio::sync::mpsc::channel(1);
    let mut command_receiver = command_receiver.unwrap_or(dummy_rx);
    let _keep_dummy_alive = dummy_tx;

    let mut peer_registry = PeerRegistry::new();
    // Rendezvous is queried once per identify, which only happens at startup.
    // Keep the nodes so discovery can be repeated: a peer that registers later,
    // or comes back after a restart, is otherwise never seen again.
    let mut rendezvous_nodes: std::collections::HashSet<PeerId> = std::collections::HashSet::new();
    let mut discovery_ticker = tokio::time::interval(REDISCOVERY_INTERVAL);
    discovery_ticker.tick().await;
    let mut pending_sync_tickets: std::collections::HashMap<
        libp2p::request_response::OutboundRequestId,
        PendingTicketSync,
    > = std::collections::HashMap::new();
    let mut pending_chat_messages: std::collections::HashMap<
        libp2p::request_response::OutboundRequestId,
        tokio::sync::oneshot::Sender<Result<ChatAckWire, String>>,
    > = std::collections::HashMap::new();
    type PendingSyncDial = (
        TicketSyncRequest,
        tokio::sync::oneshot::Sender<Result<TicketSyncResponse, String>>,
        Option<String>,
    );
    type PendingChatDial = (
        ChatMessageWire,
        tokio::sync::oneshot::Sender<Result<ChatAckWire, String>>,
    );
    type PendingFileDial = (
        String,
        std::path::PathBuf,
        tokio::sync::oneshot::Sender<Result<fortiq_core::AttachmentRecord, String>>,
        Option<String>,
        Option<OperatorSessionCertificate>,
        Option<Arc<Ed25519Signer>>,
    );

    let mut pending_shell_next: std::collections::HashMap<PeerId, Vec<PendingShellNext>> =
        std::collections::HashMap::new();
    let mut pending_sync_dials: std::collections::HashMap<PeerId, Vec<PendingSyncDial>> =
        std::collections::HashMap::new();
    let mut pending_chat_dials: std::collections::HashMap<PeerId, Vec<PendingChatDial>> =
        std::collections::HashMap::new();
    let mut pending_file_dials: std::collections::HashMap<PeerId, Vec<PendingFileDial>> =
        std::collections::HashMap::new();
    let incoming_file_slots = Arc::new(tokio::sync::Semaphore::new(MAX_PARALLEL_FILES));

    loop {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {
                info!("shutdown requested");
                return Ok(());
            }
            Some(cmd) = command_receiver.recv() => {
                match cmd {
                    P2pCommand::ListPeers { reply } => {
                        let namespace = rendezvous::Namespace::from_static("fortiq");
                        for node in &rendezvous_nodes {
                            if swarm.is_connected(node) {
                                swarm.behaviour_mut().rendezvous_client.discover(
                                    Some(namespace.clone()),
                                    None,
                                    None,
                                    *node,
                                );
                            }
                        }
                        let _ = reply.send(peer_registry.to_summaries());
                    }
                    P2pCommand::OpenShellStream { peer, ticket_id, dial: _, reply } => {
                        let _ = reply.send(Err(
                            "legacy shell/2.0 is disabled; use the canonical shell/next handshake through the service IPC".to_string(),
                        ));
                        warn!(remote_peer_id = %peer, ticket_id = ?ticket_id, "legacy shell/2.0 request rejected");
                    }
                    P2pCommand::OpenShellNext(command) => {
                        let OpenShellNextCommand {
                            peer,
                            ticket_id,
                            certificate,
                            session_signer,
                            dial,
                            reply,
                        } = *command;
                        if swarm.is_connected(&peer) {
                            spawn_open_shell_next(
                                peer,
                                local_info.peer_id.parse().expect("local PeerId"),
                                ticket_id,
                                certificate,
                                session_signer,
                                stream_control.clone(),
                                reply,
                            );
                        } else {
                            pending_shell_next
                                .entry(peer)
                                .or_default()
                                .push(PendingShellNext {
                                    ticket_id,
                                    certificate,
                                    session_signer,
                                    reply,
                                });
                            let dial_started = if let Some(addr) = dial {
                                match swarm.dial(addr) {
                                    Ok(()) => true,
                                    Err(error) => {
                                        warn!(remote_peer_id = %peer, %error, "shell/next dial failed");
                                        false
                                    }
                                }
                            } else {
                                dial_peer_candidates(
                                    swarm,
                                    peer,
                                    &peer_registry,
                                    config.network.relay_peer.as_deref(),
                                )
                            };
                            if !dial_started {
                                if let Some(pending) = pending_shell_next.remove(&peer) {
                                    for request in pending {
                                        let _ = request.reply.send(Err(
                                            "Impossible de joindre le pair distant: aucune adresse de dial disponible"
                                                .to_string(),
                                        ));
                                    }
                                }
                            }
                        }
                    }
                    P2pCommand::SyncTickets { peer, dial, request, reply } => {
                        let outbox_id = if is_ticket_mutation(&request) {
                            match serde_json::to_string(&request)
                                .map_err(anyhow::Error::from)
                                .and_then(|payload| ticket_store.enqueue_outbox(
                                    &peer.to_string(),
                                    "TICKET_SYNC",
                                    &payload,
                                )) {
                                Ok(id) => Some(id),
                                Err(error) => {
                                    let _ = reply.send(Err(format!("Échec de persistance de la synchronisation: {error}")));
                                    continue;
                                }
                            }
                        } else {
                            None
                        };
                        if swarm.is_connected(&peer) {
                            let req_id = swarm.behaviour_mut().ticket_v2.send_request(&peer, request);
                            pending_sync_tickets.insert(req_id, PendingTicketSync { reply, outbox_id });
                        } else {
                            pending_sync_dials.entry(peer).or_default().push((request, reply, outbox_id));
                            if let Some(addr) = dial {
                                let _ = swarm.dial(addr);
                            } else {
                                dial_peer_candidates(swarm, peer, &peer_registry, config.network.relay_peer.as_deref());
                            }
                        }
                    }
                    P2pCommand::SendChatMessage { peer, dial, message, reply } => {
                        if swarm.is_connected(&peer) {
                            let req_id = swarm.behaviour_mut().chat.send_request(&peer, message);
                            pending_chat_messages.insert(req_id, reply);
                        } else {
                            pending_chat_dials.entry(peer).or_default().push((message, reply));
                            if let Some(addr) = dial {
                                let _ = swarm.dial(addr);
                            } else {
                                dial_peer_candidates(swarm, peer, &peer_registry, config.network.relay_peer.as_deref());
                            }
                        }
                    }
                    P2pCommand::SendFile { peer, dial, ticket_id, file_path, certificate, session_signer, reply } => {
                        let payload = FileSendOutboxPayload {
                            ticket_id: ticket_id.clone(),
                            file_path: file_path.clone(),
                        };
                        let outbox_id = match serde_json::to_string(&payload)
                            .map_err(anyhow::Error::from)
                            .and_then(|payload| ticket_store.enqueue_outbox(
                                &peer.to_string(),
                                "FILE_SEND",
                                &payload,
                            )) {
                            Ok(id) => Some(id),
                            Err(error) => {
                                let _ = reply.send(Err(format!("Échec de persistance de l'envoi fichier: {error}")));
                                continue;
                            }
                        };
                        if swarm.is_connected(&peer) {
                            spawn_send_file_stream(
                                peer,
                                ticket_id,
                                file_path,
                                local_info.peer_id.clone(),
                                stream_control.clone(),
                                ticket_store.clone(),
                                certificate,
                                session_signer,
                                (outbox_id, reply),
                            );
                        } else {
                            pending_file_dials.entry(peer).or_default().push((ticket_id, file_path, reply, outbox_id, certificate, session_signer));
                            if let Some(addr) = dial {
                                let _ = swarm.dial(addr);
                            } else {
                                dial_peer_candidates(swarm, peer, &peer_registry, config.network.relay_peer.as_deref());
                            }
                        }
                    }
                }
            }
            Some((remote_peer, stream)) = incoming_shells_next.next() => {
                handle_incoming_shell_next(
                    stream,
                    remote_peer,
                    authority.as_deref(),
                    &ticket_store,
                    &active_shells,
                    &local_info,
                ).await;
            }
            Some((remote_peer, stream)) = incoming_files.next() => {
                let files_dir = ticket_store.storage_dir().join("tickets");
                let t_store = ticket_store.clone();
                let local_peer_id = local_info.peer_id.clone();
                let file_authority = authority.clone();
                let file_slot = incoming_file_slots.clone().try_acquire_owned().ok();
                tokio::spawn(async move {
                    handle_incoming_file_stream(
                        stream,
                        remote_peer,
                        &local_peer_id,
                        file_authority.as_deref(),
                        t_store,
                        files_dir,
                        file_slot,
                    ).await;
                });
            }
            _ = discovery_ticker.tick() => {
                let namespace = rendezvous::Namespace::from_static("fortiq");
                for node in &rendezvous_nodes {
                    if !swarm.is_connected(node) {
                        continue;
                    }
                    let _ = swarm.behaviour_mut().rendezvous_client.register(
                        namespace.clone(),
                        *node,
                        None,
                    );
                    swarm.behaviour_mut().rendezvous_client.discover(
                        Some(namespace.clone()),
                        None,
                        None,
                        *node,
                );
            }
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
                    if address.iter().any(|p| matches!(p, libp2p::multiaddr::Protocol::P2pCircuit)) {
                        info!(%address, "adding circuit address to external addresses");
                        swarm.add_external_address(address.clone());
                        let namespace = rendezvous::Namespace::from_static("fortiq");
                        for r_node in &rendezvous_nodes {
                            if let Err(error) = swarm.behaviour_mut().rendezvous_client.register(
                                namespace.clone(),
                                *r_node,
                                None,
                            ) {
                                warn!(remote_peer_id = %r_node, %error, "rendezvous registration could not start");
                            }
                        }
                    }
                }
                libp2p::swarm::SwarmEvent::ConnectionEstablished { peer_id, endpoint, .. } => {
                    info!(remote_peer_id = %peer_id, ?endpoint, "authenticated connection established");
                    peer_registry.record_connection(peer_id, &endpoint);
                    if let Some(pending) = pending_shell_next.remove(&peer_id) {
                        for request in pending {
                            spawn_open_shell_next(
                                peer_id,
                                local_info.peer_id.parse().expect("local PeerId"),
                                request.ticket_id,
                                request.certificate,
                                request.session_signer,
                                stream_control.clone(),
                                request.reply,
                            );
                        }
                    }
                    if let Some(pending) = pending_sync_dials.remove(&peer_id) {
                        for (req, reply, outbox_id) in pending {
                            let req_id = swarm.behaviour_mut().ticket_v2.send_request(&peer_id, req);
                            pending_sync_tickets.insert(req_id, PendingTicketSync { reply, outbox_id });
                        }
                    }
                    match ticket_store.list_outbox_for_peer(&peer_id.to_string()) {
                        Ok(records) => {
                            for record in records.into_iter().filter(|record| record.kind == "TICKET_SYNC") {
                                match serde_json::from_str::<TicketSyncRequest>(&record.payload) {
                                    Ok(request) => {
                                        let request_id = swarm.behaviour_mut().ticket_v2.send_request(&peer_id, request);
                                        let (reply, _response) = tokio::sync::oneshot::channel();
                                        pending_sync_tickets.insert(request_id, PendingTicketSync {
                                            reply,
                                            outbox_id: Some(record.id),
                                        });
                                    }
                                    Err(error) => {
                                        warn!(outbox_id = %record.id, %error, "dropping invalid ticket sync outbox payload");
                                        let _ = ticket_store.remove_outbox(&record.id);
                                    }
                                }
                            }
                        }
                        Err(error) => warn!(remote_peer_id = %peer_id, %error, "failed to load ticket sync outbox"),
                    }
                    if let Some(pending) = pending_chat_dials.remove(&peer_id) {
                        for (msg, reply) in pending {
                            let req_id = swarm.behaviour_mut().chat.send_request(&peer_id, msg);
                            pending_chat_messages.insert(req_id, reply);
                        }
                    }
                    match ticket_store.list_pending_messages_for_peer(
                        &local_info.peer_id,
                        &peer_id.to_string(),
                    ) {
                        Ok(messages) => {
                            for message in messages {
                                let message_id = message.id.clone();
                                let wire = ChatMessageWire {
                                    id: message.id,
                                    ticket_id: message.ticket_id,
                                    body: message.body,
                                    created_at: message.created_at,
                                    authority: None,
                                };
                                let request_id =
                                    swarm.behaviour_mut().chat.send_request(&peer_id, wire);
                                let (reply, response) = tokio::sync::oneshot::channel();
                                pending_chat_messages.insert(request_id, reply);
                                let db = ticket_store.clone();
                                tokio::spawn(async move {
                                    if let Ok(Ok(ack)) = response.await {
                                        if ack.success {
                                            let _ = db.update_message_delivery(
                                                &message_id,
                                                "DELIVERED",
                                            );
                                        }
                                    }
                                });
                            }
                        }
                        Err(error) => warn!(remote_peer_id = %peer_id, %error, "failed to load pending chat outbox"),
                    }
                    let mut active_file_outbox = std::collections::HashSet::new();
                    if let Some(pending) = pending_file_dials.remove(&peer_id) {
                        for (tid, file_path, reply, outbox_id, certificate, session_signer) in pending {
                            if let Some(id) = outbox_id.as_ref() {
                                active_file_outbox.insert(id.clone());
                            }
                            spawn_send_file_stream(
                                peer_id,
                                tid,
                                file_path,
                                local_info.peer_id.clone(),
                                stream_control.clone(),
                                ticket_store.clone(),
                                certificate,
                                session_signer,
                                (outbox_id, reply),
                            );
                        }
                    }
                    match ticket_store.list_outbox_for_peer(&peer_id.to_string()) {
                        Ok(records) => {
                            for record in records.into_iter().filter(|record| {
                                record.kind == "FILE_SEND"
                                    && !active_file_outbox.contains(&record.id)
                            }) {
                                match serde_json::from_str::<FileSendOutboxPayload>(&record.payload) {
                                    Ok(payload) => {
                                        let (reply, _response) = tokio::sync::oneshot::channel();
                                        spawn_send_file_stream(
                                            peer_id,
                                            payload.ticket_id,
                                            payload.file_path,
                                            local_info.peer_id.clone(),
                                            stream_control.clone(),
                                            ticket_store.clone(),
                                            None,
                                            None,
                                            (Some(record.id), reply),
                                        );
                                    }
                                    Err(error) => {
                                        warn!(outbox_id = %record.id, %error, "dropping invalid file outbox payload");
                                        let _ = ticket_store.remove_outbox(&record.id);
                                    }
                                }
                            }
                        }
                        Err(error) => warn!(remote_peer_id = %peer_id, %error, "failed to load file outbox"),
                    }
                    if endpoint.is_dialer() {
                        swarm.behaviour_mut().hello.send_request(
                            &peer_id,
                            HelloRequest(local_info.clone()),
                        );
                    }
                }
                libp2p::swarm::SwarmEvent::ConnectionClosed { peer_id, num_established, .. } => {
                    peer_registry.record_disconnection(&peer_id, num_established);
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Hello(event)) => {
                    handle_hello(event, swarm, &local_info, &mut peer_registry);
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::TicketV2(event)) => {
                    handle_ticket_v2(
                        event,
                        swarm,
                        &local_info.peer_id,
                        authority.as_deref(),
                        &ticket_store,
                        &mut pending_sync_tickets,
                    ).await;
                }
                libp2p::swarm::SwarmEvent::Behaviour(BehaviourEvent::Chat(event)) => {
                    handle_chat(
                        event,
                        swarm,
                        &local_info.peer_id,
                        authority.as_deref(),
                        &ticket_store,
                        &mut pending_chat_messages,
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
                            let namespace = rendezvous::Namespace::from_static("fortiq");
                            for r_node in &rendezvous_nodes {
                                if let Err(error) = swarm.behaviour_mut().rendezvous_client.register(
                                    namespace.clone(),
                                    *r_node,
                                    None,
                                ) {
                                    warn!(remote_peer_id = %r_node, %error, "rendezvous re-registration failed");
                                }
                            }
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
                    let is_loopback = info.observed_addr.iter().any(|proto| match proto {
                        libp2p::multiaddr::Protocol::Ip4(ip) => ip.is_loopback(),
                        libp2p::multiaddr::Protocol::Ip6(ip) => ip.is_loopback(),
                        _ => false,
                    });
                    if !is_loopback && config.network.relay_peer.is_none() {
                        swarm.add_external_address(info.observed_addr.clone());
                    }
                    if info
                        .protocols
                        .iter()
                        .any(|protocol| protocol.as_ref() == "/rendezvous/1.0.0")
                    {
                        let namespace = rendezvous::Namespace::from_static("fortiq");
                        rendezvous_nodes.insert(peer_id);
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
                libp2p::swarm::SwarmEvent::ListenerClosed { listener_id, reason, .. } => {
                    info!(?listener_id, ?reason, "listener closed");
                }
                libp2p::swarm::SwarmEvent::ListenerError { listener_id, error } => {
                    warn!(?listener_id, %error, "listener failed");
                }
                libp2p::swarm::SwarmEvent::OutgoingConnectionError { peer_id, error, .. } => {
                    warn!(?peer_id, %error, "outgoing connection failed");
                    if let Some(peer) = peer_id {
                        if !swarm.is_connected(&peer) {
                            if let Some(pending) = pending_shell_next.remove(&peer) {
                                for request in pending {
                                    let reply = request.reply;
                                    let _ = reply.send(Err(format!(
                                        "Impossible d'établir la connexion avec le poste distant: {error}"
                                    )));
                                }
                            }
                            if let Some(pending) = pending_sync_dials.remove(&peer) {
                                for (_, reply, _) in pending {
                                    let _ = reply.send(Err(format!(
                                        "Impossible d'établir la connexion avec le poste distant: {error}"
                                    )));
                                }
                            }
                            if let Some(pending) = pending_chat_dials.remove(&peer) {
                                for (_, reply) in pending {
                                    let _ = reply.send(Err(format!(
                                        "Impossible d'établir la connexion avec le poste distant: {error}"
                                    )));
                                }
                            }
                            if let Some(pending) = pending_file_dials.remove(&peer) {
                                for (_, _, reply, _, _, _) in pending {
                                    let _ = reply.send(Err(format!(
                                        "Impossible d'établir la connexion avec le poste distant: {error}"
                                    )));
                                }
                            }
                        }
                    }
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
                } else if !swarm.is_connected(&peer_id) {
                    // Connecting completes peer inventory; transport never
                    // grants application authority.
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

async fn handle_ticket_v2(
    event: request_response::Event<TicketSyncRequest, TicketSyncResponse>,
    swarm: &mut Swarm<Behaviour>,
    local_peer_id: &str,
    authority: Option<&AuthorityContext>,
    ticket_store: &TicketDb,
    pending_sync_tickets: &mut std::collections::HashMap<
        request_response::OutboundRequestId,
        PendingTicketSync,
    >,
) {
    match event {
        request_response::Event::Message { peer, message, .. } => {
            match message {
                request_response::Message::Request {
                    request, channel, ..
                } => {
                    let response = match request {
                        TicketSyncRequest::GetTickets {
                            authority: request_authority,
                        } => {
                            let can_read = authority
                                .as_ref()
                                .zip(request_authority.as_ref())
                                .is_some_and(|(context, proof)| {
                                    verify_operator_session_proof(
                                        context,
                                        &peer,
                                        proof,
                                        "ticket-read",
                                        "*",
                                        "*",
                                        0,
                                        b"",
                                        OperatorCapabilities::READ,
                                    )
                                });
                            let tickets = ticket_store
                                .list_tickets(None)
                                .unwrap_or_default()
                                .into_iter()
                                .filter(|ticket| {
                                    ticket.client_peer_id == peer.to_string()
                                        || (ticket.client_peer_id == local_peer_id && can_read)
                                })
                                .collect();
                            TicketSyncResponse::Tickets(tickets)
                        }
                        TicketSyncRequest::GetTicket {
                            ticket_id,
                            authority: request_authority,
                        } => {
                            let can_read = authority
                                .as_ref()
                                .zip(request_authority.as_ref())
                                .is_some_and(|(context, proof)| {
                                    verify_operator_session_proof(
                                        context,
                                        &peer,
                                        proof,
                                        "ticket-read",
                                        &ticket_id,
                                        &ticket_id,
                                        0,
                                        b"",
                                        OperatorCapabilities::READ,
                                    )
                                });
                            let ticket = ticket_store.get_ticket(&ticket_id).ok().flatten().filter(
                                |ticket| {
                                    ticket.client_peer_id == peer.to_string()
                                        || (ticket.client_peer_id == local_peer_id && can_read)
                                },
                            );
                            TicketSyncResponse::Ticket(ticket)
                        }
                        TicketSyncRequest::PushTicket(ticket) => {
                            let remote = peer.to_string();
                            let authorized = ticket.client_peer_id == remote
                                && ticket.state == fortiq_core::TicketState::Open
                                && ticket.revision == 1
                                && ticket.closed_at.is_none()
                                && ticket_store.get_ticket(&ticket.id).ok().flatten().is_none();
                            if !authorized {
                                TicketSyncResponse::MutationRejected {
                                    kind: MutationRejectionKind::Permanent,
                                    message: "PeerId non autorisé à publier ce ticket".to_string(),
                                    canonical: None,
                                }
                            } else {
                                match ticket_store.import_canonical_ticket(&ticket) {
                                    Ok(()) => TicketSyncResponse::MutationApplied(ticket),
                                    Err(error) => TicketSyncResponse::MutationRejected {
                                        kind: MutationRejectionKind::Conflict,
                                        message: format!("Erreur: {error}"),
                                        canonical: ticket_store
                                            .get_ticket(&ticket.id)
                                            .ok()
                                            .flatten()
                                            .map(Box::new),
                                    },
                                }
                            }
                        }
                        TicketSyncRequest::UpdateStatusSigned(mutation) => {
                            let current =
                                ticket_store.get_ticket(&mutation.ticket_id).ok().flatten();
                            let authorized = current.as_ref().is_some_and(|ticket| {
                            authority.is_some_and(|authority| {
                                mutation.network_id == authority.network_id
                                    && mutation.operator_transport_peer_id == peer.to_string()
                                    && authority.verify_certificate(
                                        &peer,
                                        &mutation.certificate,
                                        OperatorCapabilities::TICKET_MANAGE,
                                        now_secs(),
                                    )
                            })
                                && Ed25519Verifier::from_public_key(&mutation.certificate.session_pubkey)
                                    .and_then(|verifier| {
                                        Ok(verifier.verify(&mutation.signing_payload(), &mutation.signature)?)
                                    })
                                    .is_ok()
                                && ticket.client_peer_id == local_peer_id
                                && ticket.revision == mutation.expected_revision
                        });
                            if !authorized {
                                TicketSyncResponse::MutationRejected {
                                    kind: MutationRejectionKind::Permanent,
                                    message: "Signed lifecycle mutation rejected".to_string(),
                                    canonical: current.map(Box::new),
                                }
                            } else {
                                match ticket_store.update_ticket_state(
                                    &mutation.ticket_id,
                                    mutation.new_state,
                                    &peer.to_string(),
                                ) {
                                    Ok(ticket) => {
                                        TicketSyncResponse::MutationApplied(Box::new(ticket))
                                    }
                                    Err(error) => TicketSyncResponse::MutationRejected {
                                        kind: MutationRejectionKind::Conflict,
                                        message: error.to_string(),
                                        canonical: current.map(Box::new),
                                    },
                                }
                            }
                        }
                    };
                    let _ = swarm
                        .behaviour_mut()
                        .ticket_v2
                        .send_response(channel, response);
                }
                request_response::Message::Response {
                    request_id,
                    response,
                } => {
                    if let Some(pending) = pending_sync_tickets.remove(&request_id) {
                        let terminal = matches!(
                            response,
                            TicketSyncResponse::MutationApplied(_)
                                | TicketSyncResponse::MutationRejected { .. }
                        );
                        if terminal {
                            if let Some(outbox_id) = pending.outbox_id.as_deref() {
                                let _ = ticket_store.remove_outbox(outbox_id);
                            }
                        }
                        match &response {
                            TicketSyncResponse::MutationApplied(ticket) => {
                                let _ = ticket_store.import_canonical_ticket(ticket);
                            }
                            TicketSyncResponse::MutationRejected {
                                canonical: Some(ticket),
                                ..
                            } if ticket.client_peer_id != local_peer_id => {
                                let _ = ticket_store.import_canonical_ticket(ticket);
                            }
                            _ => {}
                        }
                        let _ = pending.reply.send(Ok(response));
                    }
                }
            }
        }
        request_response::Event::OutboundFailure {
            request_id, error, ..
        } => {
            if let Some(pending) = pending_sync_tickets.remove(&request_id) {
                let _ = pending
                    .reply
                    .send(Err(format!("Échec de la requête ticket sync: {error}")));
            }
        }
        _ => {}
    }
}

async fn handle_chat(
    event: request_response::Event<ChatMessageWire, ChatAckWire>,
    swarm: &mut Swarm<Behaviour>,
    local_peer_id: &str,
    authority: Option<&AuthorityContext>,
    ticket_store: &TicketDb,
    pending_chat_messages: &mut std::collections::HashMap<
        request_response::OutboundRequestId,
        tokio::sync::oneshot::Sender<Result<ChatAckWire, String>>,
    >,
) {
    match event {
        request_response::Event::Message { peer, message, .. } => match message {
            request_response::Message::Request {
                request, channel, ..
            } => {
                let authority_valid = match ticket_store.get_ticket(&request.ticket_id) {
                    Ok(Some(ticket)) if ticket.client_peer_id == local_peer_id => {
                        match request.authority.as_ref() {
                            Some(proof) => match authority {
                                Some(authority) => verify_operator_session_proof(
                                    authority,
                                    &peer,
                                    proof,
                                    "chat",
                                    &request.ticket_id,
                                    &request.id,
                                    request.created_at,
                                    request.body.as_bytes(),
                                    OperatorCapabilities::WRITE,
                                ),
                                None => false,
                            },
                            None => false,
                        }
                    }
                    _ => true,
                };
                let ack = match ticket_store.get_ticket(&request.ticket_id) {
                    Ok(Some(ticket)) => {
                        if !is_ticket_counterparty(&ticket, local_peer_id, &peer) {
                            ChatAckWire {
                                message_id: request.id,
                                success: false,
                                error: Some("PeerId non autorisé pour ce ticket".to_string()),
                            }
                        } else if !authority_valid {
                            ChatAckWire {
                                message_id: request.id,
                                success: false,
                                error: Some("Authority WRITE invalide".to_string()),
                            }
                        } else if !ticket.state.permits_work() {
                            ChatAckWire {
                                message_id: request.id,
                                success: false,
                                error: Some("Le ticket est fermé".to_string()),
                            }
                        } else {
                            let chat_msg = fortiq_core::ChatMessage {
                                id: request.id.clone(),
                                ticket_id: request.ticket_id.clone(),
                                sender_peer_id: peer.to_string(),
                                body: request.body.clone(),
                                created_at: request.created_at,
                                delivery_state: "DELIVERED".to_string(),
                            };
                            if let Err(error) = ticket_store.add_chat_message(&chat_msg) {
                                ChatAckWire {
                                    message_id: request.id,
                                    success: false,
                                    error: Some(format!(
                                        "Échec de persistance du message: {error}"
                                    )),
                                }
                            } else {
                                let preview: String = request.body.chars().take(40).collect();
                                let _ = ticket_store.record_event(
                                    &request.ticket_id,
                                    "CHAT_MESSAGE_RECEIVED",
                                    &peer.to_string(),
                                    Some(&format!("{}: {}", peer, preview)),
                                );
                                ChatAckWire {
                                    message_id: request.id,
                                    success: true,
                                    error: None,
                                }
                            }
                        }
                    }
                    _ => ChatAckWire {
                        message_id: request.id,
                        success: false,
                        error: Some("Ticket introuvable sur le poste distant".to_string()),
                    },
                };
                let _ = swarm.behaviour_mut().chat.send_response(channel, ack);
            }
            request_response::Message::Response {
                request_id,
                response,
            } => {
                if let Some(reply) = pending_chat_messages.remove(&request_id) {
                    let _ = reply.send(Ok(response));
                }
            }
        },
        request_response::Event::OutboundFailure {
            request_id, error, ..
        } => {
            if let Some(reply) = pending_chat_messages.remove(&request_id) {
                let _ = reply.send(Err(format!("Échec de l'envoi du message chat: {error}")));
            }
        }
        _ => {}
    }
}

async fn handle_incoming_shell_next(
    mut stream: libp2p::Stream,
    remote_peer: PeerId,
    authority: Option<&AuthorityContext>,
    ticket_store: &TicketDb,
    active_shells: &Arc<AtomicBool>,
    local_info: &NodeInfo,
) {
    use futures::AsyncWriteExt;

    let mut challenge = [0u8; 32];
    OsRng.fill_bytes(&mut challenge);
    if stream.write_all(&challenge).await.is_err() || stream.flush().await.is_err() {
        return;
    }
    let handshake = match tokio::time::timeout(
        Duration::from_secs(5),
        fortiq_shell::ShellNextHandshake::read_from_async(&mut stream),
    )
    .await
    {
        Ok(Ok(handshake)) => handshake,
        _ => {
            let _ = fortiq_shell::send_authorization_code(
                &mut stream,
                fortiq_shell::DENIED_INVALID_AUTHORITY,
            )
            .await;
            return;
        }
    };

    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let payload = fortiq_shell::ShellNextHandshake::signing_payload(
        &handshake.network_id,
        &handshake.ticket_id,
        &handshake.operator_transport_peer_id,
        &challenge,
    );
    let session_verifier =
        Ed25519Verifier::from_public_key(&handshake.session_certificate.session_pubkey);
    let authority_valid = authority.is_some_and(|authority| {
        handshake.network_id == authority.network_id
            && handshake.operator_transport_peer_id == remote_peer.to_string()
            && authority.verify_certificate(
                &remote_peer,
                &handshake.session_certificate,
                OperatorCapabilities::SHELL_EXEC,
                now,
            )
    }) && session_verifier
        .and_then(|verifier| Ok(verifier.verify(&payload, &handshake.challenge_signature)?))
        .is_ok();
    let ticket = match ticket_store.get_ticket(&handshake.ticket_id).ok().flatten() {
        Some(ticket) => ticket,
        None => {
            let _ =
                fortiq_shell::send_authorization_code(&mut stream, fortiq_shell::DENIED_NO_TICKET)
                    .await;
            return;
        }
    };
    if !authority_valid {
        let _ = fortiq_shell::send_authorization_code(
            &mut stream,
            fortiq_shell::DENIED_INVALID_AUTHORITY,
        )
        .await;
        return;
    }
    if ticket.client_peer_id != local_info.peer_id || !ticket.state.permits_work() {
        let _ =
            fortiq_shell::send_authorization_code(&mut stream, fortiq_shell::DENIED_TICKET_CLOSED)
                .await;
        return;
    }
    if active_shells
        .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
        .is_err()
    {
        let _ = fortiq_shell::send_authorization_code(&mut stream, fortiq_shell::DENIED_BUSY).await;
        return;
    }
    if fortiq_shell::send_authorization(&mut stream, true)
        .await
        .is_err()
    {
        active_shells.store(false, Ordering::SeqCst);
        return;
    }

    info!(remote_peer_id = %remote_peer, ticket_id = %ticket.id, "accepted canonical shell next");
    let info = local_info.clone();
    let shells_flag = active_shells.clone();
    let session_id = uuid::Uuid::new_v4().to_string();
    let db = ticket_store.clone();
    let ticket_id = ticket.id.clone();
    let operator_transport = remote_peer.to_string();
    let watched_certificate_expires_at = handshake.session_certificate.expires_at;
    tokio::spawn(async move {
        let _guard = ShellSessionGuard(shells_flag);
        let _ = db.record_shell_session_start(
            &session_id,
            &ticket_id,
            &operator_transport,
            "QUIC/shell-next",
        );
        let watcher_db = db.clone();
        let watched_ticket = ticket_id.clone();
        let result = tokio::select! {
            result = fortiq_shell::serve(stream, info) => result,
            () = async move {
                loop {
                    tokio::time::sleep(Duration::from_millis(250)).await;
                    let valid = watcher_db.get_ticket(&watched_ticket).ok().flatten().is_some_and(|ticket| {
                        ticket.state.permits_work()
                    }) && std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap_or_default()
                        .as_secs() < watched_certificate_expires_at;
                    if !valid { break; }
                }
            } => Err(anyhow::anyhow!("ticket lifecycle or operator session expired")),
        };
        let _ = db.record_shell_session_end(
            &session_id,
            Some(if result.is_ok() {
                "SUCCESS"
            } else {
                "REVOKED_OR_ERROR"
            }),
        );
    });
}

#[allow(clippy::too_many_arguments)]
async fn handle_incoming_file_stream(
    mut stream: libp2p::Stream,
    remote_peer: PeerId,
    local_peer_id: &str,
    authority: Option<&AuthorityContext>,
    ticket_store: TicketDb,
    files_base_dir: std::path::PathBuf,
    file_slot: Option<tokio::sync::OwnedSemaphorePermit>,
) {
    use futures::{AsyncReadExt, AsyncWriteExt};
    use sha2::{Digest, Sha256};
    use tokio::io::AsyncWriteExt as TokioAsyncWriteExt;

    let Some(_file_slot) = file_slot else {
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    };

    let mut len_bytes = [0u8; 2];
    if !matches!(
        tokio::time::timeout(FILE_IDLE_TIMEOUT, stream.read_exact(&mut len_bytes)).await,
        Ok(Ok(()))
    ) {
        return;
    }
    let len = u16::from_be_bytes(len_bytes) as usize;
    let mut buf = vec![0u8; len];
    if !matches!(
        tokio::time::timeout(FILE_IDLE_TIMEOUT, stream.read_exact(&mut buf)).await,
        Ok(Ok(()))
    ) {
        return;
    }
    let offer: FileOfferWire = match serde_json::from_slice(&buf) {
        Ok(o) => o,
        Err(_) => return,
    };
    let file_id = match normalize_file_id(&offer.file_id) {
        Some(id) => id,
        None => {
            warn!(remote_peer_id = %remote_peer, "denied file with invalid file id");
            let _ = stream.write_all(&[FILE_DENIED]).await;
            return;
        }
    };

    let ticket = match ticket_store.get_ticket(&offer.ticket_id) {
        Ok(Some(t)) => t,
        _ => {
            let _ = stream.write_all(&[FILE_DENIED_NO_TICKET]).await;
            return;
        }
    };

    if !is_ticket_counterparty(&ticket, local_peer_id, &remote_peer) {
        warn!(remote_peer_id = %remote_peer, ticket_id = %offer.ticket_id, "denied file from non-counterparty");
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    }

    if ticket.client_peer_id == local_peer_id {
        let content = format!("{}:{}:{}", offer.filename, offer.file_size, offer.sha256);
        let valid = match offer.authority.as_ref() {
            Some(proof) => match authority {
                Some(authority) => verify_operator_session_proof(
                    authority,
                    &remote_peer,
                    proof,
                    "file",
                    &offer.ticket_id,
                    &offer.file_id,
                    0,
                    content.as_bytes(),
                    OperatorCapabilities::FILE_TRANSFER,
                ),
                None => false,
            },
            None => false,
        };
        if !valid {
            warn!(remote_peer_id = %remote_peer, ticket_id = %offer.ticket_id, "denied file with invalid FILE_TRANSFER authority");
            let _ = stream.write_all(&[FILE_DENIED]).await;
            return;
        }
    }

    if !ticket.state.permits_work() {
        let _ = stream.write_all(&[FILE_DENIED_TICKET_CLOSED]).await;
        return;
    }

    if offer.file_size > MAX_FILE_SIZE {
        let _ = stream.write_all(&[FILE_DENIED_TOO_LARGE]).await;
        return;
    }

    let stored_bytes = ticket_store
        .list_attachments(&offer.ticket_id)
        .unwrap_or_default()
        .into_iter()
        .map(|attachment| attachment.size_bytes)
        .sum::<u64>();
    if stored_bytes.saturating_add(offer.file_size) > MAX_TICKET_STORAGE {
        warn!(ticket_id = %offer.ticket_id, stored_bytes, offered_bytes = offer.file_size, "denied file: ticket storage quota exceeded");
        let _ = stream.write_all(&[FILE_DENIED_TOO_LARGE]).await;
        return;
    }

    let original_name = std::path::Path::new(&offer.filename)
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("file.bin");
    let safe_name = original_name.replace(['/', '\\', ':', '*', '?', '"', '<', '>', '|'], "_");

    let ticket_storage_key = format!("{:x}", Sha256::digest(offer.ticket_id.as_bytes()));
    let ticket_dir = files_base_dir.join(ticket_storage_key).join("files");
    if let Err(e) = tokio::fs::create_dir_all(&ticket_dir).await {
        warn!(%e, "failed to create files dir for ticket");
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    }

    if stream.write_all(&[FILE_ACCEPT]).await.is_err() {
        return;
    }
    let _ = stream.flush().await;

    let part_path = ticket_dir.join(format!("{file_id}.part"));
    let final_path = ticket_dir.join(format!("{file_id}_{safe_name}"));

    let mut part_file = match tokio::fs::File::create(&part_path).await {
        Ok(f) => f,
        Err(e) => {
            warn!(%e, "failed to create part file");
            return;
        }
    };

    let mut hasher = Sha256::new();
    let mut remaining = offer.file_size;
    let mut chunk_buf = [0u8; 8192];
    let mut success = true;

    while remaining > 0 {
        let to_read = std::cmp::min(remaining, chunk_buf.len() as u64) as usize;
        match tokio::time::timeout(
            FILE_IDLE_TIMEOUT,
            stream.read_exact(&mut chunk_buf[..to_read]),
        )
        .await
        {
            Ok(Ok(())) => {
                hasher.update(&chunk_buf[..to_read]);
                if let Err(e) = part_file.write_all(&chunk_buf[..to_read]).await {
                    warn!(%e, "failed to write chunk to part file");
                    success = false;
                    break;
                }
                remaining -= to_read as u64;
            }
            Ok(Err(e)) => {
                warn!(%e, "error reading file chunk from stream");
                success = false;
                break;
            }
            Err(_) => {
                warn!(ticket_id = %offer.ticket_id, "file transfer idle timeout");
                success = false;
                break;
            }
        }
    }

    let _ = part_file.flush().await;
    drop(part_file);

    if !success {
        let _ = tokio::fs::remove_file(&part_path).await;
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    }

    let computed_hash = format!("{:x}", hasher.finalize());
    if computed_hash != offer.sha256 {
        warn!(computed = %computed_hash, expected = %offer.sha256, "file SHA-256 mismatch");
        let _ = tokio::fs::remove_file(&part_path).await;
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    }

    if let Err(e) = tokio::fs::rename(&part_path, &final_path).await {
        warn!(%e, "failed to rename part file to final path");
        let _ = tokio::fs::remove_file(&part_path).await;
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    }

    let attachment = fortiq_core::AttachmentRecord {
        id: file_id,
        ticket_id: offer.ticket_id.clone(),
        sender_peer_id: remote_peer.to_string(),
        filename: safe_name.clone(),
        size_bytes: offer.file_size,
        sha256: computed_hash,
        local_path: final_path.to_string_lossy().to_string(),
        created_at: std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs(),
        state: "STORED".to_string(),
    };

    if let Err(error) = ticket_store.add_attachment(&attachment) {
        warn!(%error, "failed to persist received attachment metadata");
        let _ = tokio::fs::remove_file(&final_path).await;
        let _ = stream.write_all(&[FILE_DENIED]).await;
        return;
    }
    let _ = ticket_store.record_event(
        &offer.ticket_id,
        "ATTACHMENT_RECEIVED",
        &remote_peer.to_string(),
        Some(&format!("Fichier reçu de {}: {safe_name}", remote_peer)),
    );

    let _ = stream.write_all(&[FILE_ACCEPT]).await;
    let _ = stream.flush().await;
}

fn normalize_file_id(file_id: &str) -> Option<String> {
    uuid::Uuid::parse_str(file_id)
        .ok()
        .map(|id| id.simple().to_string())
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
    println!("remote_os = {}", info.os);
    println!("remote_arch = {}", info.arch);
    println!("remote_version = {}", info.version);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validate_hello_accepts_matching_peer_id() {
        let peer_id = PeerId::random();
        let info = NodeInfo::local(peer_id, "node".to_owned());
        assert!(validate_hello(&peer_id, &info).is_ok());
    }

    #[test]
    fn validate_hello_rejects_mismatched_peer_id() {
        let auth_peer = PeerId::random();
        let claimed_peer = PeerId::random();
        let info = NodeInfo::local(claimed_peer, "node".to_owned());
        let error = validate_hello(&auth_peer, &info).unwrap_err();
        assert!(error.to_string().contains("HELLO PeerId mismatch"));
    }

    #[test]
    fn file_id_must_be_a_uuid_and_cannot_traverse_paths() {
        let id = uuid::Uuid::new_v4();
        assert_eq!(
            normalize_file_id(&id.to_string()),
            Some(id.simple().to_string())
        );
        assert_eq!(normalize_file_id("../../authorized_keys"), None);
        assert_eq!(normalize_file_id("..\\..\\authorized_keys"), None);
    }

    #[test]
    fn client_ticket_rejects_unconfigured_remote_counterparty() {
        let local = PeerId::random();
        let configured = PeerId::random();
        let attacker = PeerId::random();
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket(
                "Support",
                "route guard",
                fortiq_core::TicketPriority::Normal,
                &local.to_string(),
            )
            .unwrap();

        assert!(is_ticket_counterparty(
            &ticket,
            &local.to_string(),
            &configured,
        ));
        assert!(is_ticket_counterparty(
            &ticket,
            &local.to_string(),
            &attacker,
        ));
    }

    #[test]
    fn operator_session_proof_payload_binds_operation_ticket_content_and_capability() {
        let payload = OperatorSessionProof::signing_payload(
            "chat",
            "FTQ-1",
            "MSG-1",
            1,
            b"hello",
            "operator-peer",
        );
        assert!(payload.starts_with(OperatorSessionProof::SIGNATURE_DOMAIN));
        assert_ne!(
            payload,
            OperatorSessionProof::signing_payload(
                "file",
                "FTQ-1",
                "FILE-1",
                0,
                b"hello",
                "operator-peer",
            )
        );
        assert_ne!(
            payload,
            OperatorSessionProof::signing_payload(
                "chat",
                "FTQ-1",
                "MSG-2",
                1,
                b"tampered",
                "operator-peer",
            )
        );
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
            format!("{relay}/p2p-circuit/p2p/{peer_id}")
        );
        assert_eq!(
            candidates[1].to_string(),
            format!("/ip4/10.0.0.5/udp/4001/quic-v1/p2p/{peer_id}")
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
        let info = NodeInfo::local(peer_id, "OFFICE-PC".to_owned());

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
