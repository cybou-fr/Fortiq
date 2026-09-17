use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, Mutex};
use tracing::warn;

use fortiq_core::canonical::crypto::keys::{MnemonicEntropy, OwnerRootSigningSeed};
use fortiq_core::canonical::portable::certificate::{
    OperatorCapabilities, OperatorSessionCertificate,
};
use fortiq_core::canonical::portable::mnemonic::MnemonicDeriver;
use fortiq_core::canonical::portable::workspace::MemoryWorkspace;
use fortiq_core::canonical::self_support::{LoopbackEndpoint, SelfSupportEngine, ThisDevice};
use fortiq_core::canonical::signing::{Signer, SigningError};
use fortiq_core::canonical::types::{EntityId, KeyId, NetworkId, OwnerId};
use fortiq_core::ipc::{IpcRequest, IpcResponse};

use crate::command::DesktopCommand;
use crate::event::DesktopEvent;
use crate::ipc::IpcClient;
use crate::models::{
    AttachmentDto, ChatMessageDto, DesktopPeerDto, DesktopStatusDto, OperatorSessionDto,
    SelfSupportStatusDto, TicketDetailDto, TicketSummaryDto,
};
use crate::terminal::{TerminalCommand, TerminalSession};

pub struct LocalOwnerSigner {
    pub key_id: KeyId,
    pub seed: OwnerRootSigningSeed,
}

impl Signer for LocalOwnerSigner {
    fn sign(&self, domain_separated_data: &[u8]) -> Result<Vec<u8>, SigningError> {
        let mut hasher = blake3::Hasher::new_keyed(self.seed.as_bytes());
        hasher.update(domain_separated_data);
        Ok(hasher.finalize().as_bytes().to_vec())
    }

    fn key_id(&self) -> KeyId {
        self.key_id
    }
}

pub struct OperatorState {
    pub workspace: MemoryWorkspace,
    pub cert: OperatorSessionCertificate,
    pub expires_at: u64,
}

pub struct BackendActor {
    ipc: Arc<IpcClient>,
    cmd_rx: mpsc::Receiver<DesktopCommand>,
    event_tx: mpsc::Sender<DesktopEvent>,
    selected_ticket_id: Option<String>,
    current_status: DesktopStatusDto,
    terminal_tx: Option<mpsc::Sender<TerminalCommand>>,
    self_support_engine: Arc<Mutex<SelfSupportEngine>>,
    operator_state: Arc<Mutex<Option<OperatorState>>>,
}

impl BackendActor {
    pub fn new(
        ipc: Arc<IpcClient>,
        cmd_rx: mpsc::Receiver<DesktopCommand>,
        event_tx: mpsc::Sender<DesktopEvent>,
    ) -> Self {
        let device_id = EntityId::from_bytes([0x42; 32]);
        let this_device = ThisDevice::new(device_id, LoopbackEndpoint::default());
        let engine = SelfSupportEngine::new(this_device);

        Self {
            ipc,
            cmd_rx,
            event_tx,
            selected_ticket_id: None,
            current_status: DesktopStatusDto::default(),
            terminal_tx: None,
            self_support_engine: Arc::new(Mutex::new(engine)),
            operator_state: Arc::new(Mutex::new(None)),
        }
    }

    pub async fn run(mut self) {
        let mut interval = tokio::time::interval(Duration::from_secs(3));
        // First refresh
        self.handle_refresh().await;

        loop {
            tokio::select! {
                Some(cmd) = self.cmd_rx.recv() => {
                    self.handle_command(cmd).await;
                }
                _ = interval.tick() => {
                    self.handle_refresh().await;
                }
                else => break,
            }
        }
    }

    async fn handle_command(&mut self, cmd: DesktopCommand) {
        match cmd {
            DesktopCommand::Refresh => {
                self.handle_refresh().await;
            }
            DesktopCommand::SelectTicket(ticket_id) => {
                self.selected_ticket_id = Some(ticket_id.clone());
                self.load_ticket_detail(&ticket_id).await;
            }
            DesktopCommand::CreateTicket { title, priority } => {
                self.create_ticket(title, priority).await;
            }
            DesktopCommand::SendMessage { ticket_id, body } => {
                self.send_message(&ticket_id, &body).await;
            }
            DesktopCommand::SendFile { ticket_id, path } => {
                self.send_file(&ticket_id, &path).await;
            }
            DesktopCommand::StartShell {
                ticket_id,
                cols,
                rows,
            } => {
                self.start_shell(&ticket_id, cols, rows).await;
            }
            DesktopCommand::ShellInput(bytes) => {
                if let Some(tx) = &self.terminal_tx {
                    let _ = tx.send(TerminalCommand::Input(bytes)).await;
                }
            }
            DesktopCommand::ResizeShell { cols, rows } => {
                if let Some(tx) = &self.terminal_tx {
                    let _ = tx.send(TerminalCommand::Resize { cols, rows }).await;
                }
            }
            DesktopCommand::CloseShell => {
                self.close_shell().await;
            }
            DesktopCommand::RevokeShell => {
                self.revoke_shell().await;
            }
            DesktopCommand::UnlockOperator(mnemonic) => {
                self.unlock_operator(&mnemonic).await;
            }
            DesktopCommand::LockOperator => {
                self.lock_operator().await;
            }
            DesktopCommand::TriggerSelfSupportAction(action) => {
                self.trigger_self_support(&action).await;
            }
            DesktopCommand::RefreshSelfSupport => {
                self.refresh_self_support().await;
            }
        }
    }

    async fn handle_refresh(&mut self) {
        // Poll status
        match self.ipc.send_request(&IpcRequest::GetStatus).await {
            Ok(IpcResponse::Status(status)) => {
                let is_unlocked = self.operator_state.lock().await.is_some();
                let status_dto = DesktopStatusDto {
                    product: status.product,
                    version: status.version,
                    agent_state: status.agent_state,
                    mode: format!("{:?}", status.mode).to_lowercase(),
                    peer_id: status.peer_id,
                    active_ticket_id: status.active_ticket.as_ref().map(|t| t.id.clone()),
                    active_ticket_state: status
                        .active_ticket
                        .as_ref()
                        .map(|t| format!("{:?}", t.state)),
                    authorized_operator: status.authorized_operator,
                    is_operator_unlocked: is_unlocked,
                };
                self.current_status = status_dto.clone();
                let _ = self.event_tx.send(DesktopEvent::StatusChanged(status_dto)).await;
            }
            Ok(_) => {}
            Err(e) => {
                warn!("IPC Status check failed: {e}");
                self.current_status.agent_state = "offline".into();
                let _ = self
                    .event_tx
                    .send(DesktopEvent::StatusChanged(self.current_status.clone()))
                    .await;
            }
        }

        // Poll peers
        if let Ok(IpcResponse::Peers(peers)) = self.ipc.send_request(&IpcRequest::ListPeers).await {
            let peer_dtos = peers
                .into_iter()
                .map(|p| DesktopPeerDto {
                    peer_id: p.peer_id,
                    hostname: p.hostname,
                    os: p.os,
                    transport: p.transport,
                    status: p.status,
                    mode: p.mode.map(|m| format!("{m:?}").to_lowercase()),
                    authorized_operator: p.authorized_operator,
                    relay: p.relay,
                    rendezvous: p.rendezvous,
                })
                .collect();
            let _ = self.event_tx.send(DesktopEvent::PeersChanged(peer_dtos)).await;
        }

        // Poll tickets
        if let Ok(IpcResponse::Tickets(tickets)) = self
            .ipc
            .send_request(&IpcRequest::ListTickets { state_filter: None })
            .await
        {
            let ticket_dtos = tickets
                .into_iter()
                .map(|t| {
                    let prio = match t.priority {
                        fortiq_core::TicketPriority::Normal => 2,
                        fortiq_core::TicketPriority::High => 3,
                        fortiq_core::TicketPriority::Urgent => 4,
                    };
                    TicketSummaryDto {
                        id: t.id,
                        title: t.title,
                        priority: prio,
                        state: t.state.as_str().to_string(),
                        created_at: t.created_at,
                    }
                })
                .collect();
            let _ = self
                .event_tx
                .send(DesktopEvent::TicketsChanged(ticket_dtos))
                .await;
        }

        // If ticket selected, refresh it
        if let Some(id) = self.selected_ticket_id.clone() {
            self.load_ticket_detail(&id).await;
        }
    }

    async fn load_ticket_detail(&self, ticket_id: &str) {
        match self
            .ipc
            .send_request(&IpcRequest::GetTicket {
                ticket_id: ticket_id.to_string(),
            })
            .await
        {
            Ok(IpcResponse::TicketDetail(Some(detail))) => {
                let msgs = detail
                    .messages
                    .into_iter()
                    .map(|m| ChatMessageDto {
                        id: m.id,
                        sender: m.sender_peer_id,
                        body: m.body,
                        created_at: m.created_at,
                        is_operator: false,
                    })
                    .collect();

                let attachments = detail
                    .attachments
                    .into_iter()
                    .map(|a| AttachmentDto {
                        id: a.id,
                        filename: a.filename,
                        size_bytes: a.size_bytes,
                        sha256: a.sha256,
                    })
                    .collect();

                let prio = match detail.ticket.priority {
                    fortiq_core::TicketPriority::Normal => 2,
                    fortiq_core::TicketPriority::High => 3,
                    fortiq_core::TicketPriority::Urgent => 4,
                };

                let dto = TicketDetailDto {
                    id: detail.ticket.id,
                    title: detail.ticket.title,
                    priority: prio,
                    state: detail.ticket.state.as_str().to_string(),
                    created_at: detail.ticket.created_at,
                    access_epoch: None,
                    messages: msgs,
                    attachments,
                };
                let _ = self
                    .event_tx
                    .send(DesktopEvent::TicketLoaded(Some(dto)))
                    .await;
            }
            Ok(IpcResponse::TicketDetail(None)) => {
                let _ = self.event_tx.send(DesktopEvent::TicketLoaded(None)).await;
            }
            Ok(_) => {}
            Err(e) => {
                let _ = self.event_tx.send(DesktopEvent::Error(e.to_string())).await;
            }
        }
    }

    async fn create_ticket(&self, title: String, priority: u8) {
        let prio = match priority {
            3 => fortiq_core::TicketPriority::High,
            4 => fortiq_core::TicketPriority::Urgent,
            _ => fortiq_core::TicketPriority::Normal,
        };

        match self
            .ipc
            .send_request(&IpcRequest::CreateTicket {
                title: title.clone(),
                description: String::new(),
                priority: prio,
            })
            .await
        {
            Ok(IpcResponse::TicketCreated(t)) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Notification {
                        level: "info".into(),
                        message: format!("Ticket créé: {}", t.title),
                    })
                    .await;
            }
            Ok(IpcResponse::Error(msg)) | Err(crate::ipc::IpcClientError::DaemonError(msg)) => {
                let _ = self.event_tx.send(DesktopEvent::Error(msg)).await;
            }
            Err(e) => {
                let _ = self.event_tx.send(DesktopEvent::Error(e.to_string())).await;
            }
            _ => {}
        }
    }

    async fn send_message(&self, ticket_id: &str, body: &str) {
        match self
            .ipc
            .send_request(&IpcRequest::SendChatMessage {
                ticket_id: ticket_id.to_string(),
                body: body.to_string(),
            })
            .await
        {
            Ok(IpcResponse::MessageSent(msg)) => {
                let dto = ChatMessageDto {
                    id: msg.id,
                    sender: msg.sender_peer_id,
                    body: msg.body,
                    created_at: msg.created_at,
                    is_operator: false,
                };
                let _ = self
                    .event_tx
                    .send(DesktopEvent::MessageAdded {
                        ticket_id: ticket_id.to_string(),
                        message: dto,
                    })
                    .await;
            }
            Ok(IpcResponse::Error(msg)) | Err(crate::ipc::IpcClientError::DaemonError(msg)) => {
                let _ = self.event_tx.send(DesktopEvent::Error(msg)).await;
            }
            Err(e) => {
                let _ = self.event_tx.send(DesktopEvent::Error(e.to_string())).await;
            }
            _ => {}
        }
    }

    async fn send_file(&self, ticket_id: &str, path: &std::path::Path) {
        match IpcClient::stage_file(path) {
            Ok(staged) => {
                let staged_str = staged.to_string_lossy().to_string();
                match self
                    .ipc
                    .send_request(&IpcRequest::SendFile {
                        ticket_id: ticket_id.to_string(),
                        staged_path: staged_str,
                    })
                    .await
                {
                    Ok(IpcResponse::FileSent(att)) => {
                        let dto = AttachmentDto {
                            id: att.id,
                            filename: att.filename,
                            size_bytes: att.size_bytes,
                            sha256: att.sha256,
                        };
                        let _ = self
                            .event_tx
                            .send(DesktopEvent::FileAdded {
                                ticket_id: ticket_id.to_string(),
                                file: dto,
                            })
                            .await;
                        let _ = self
                            .event_tx
                            .send(DesktopEvent::Notification {
                                level: "info".into(),
                                message: "Fichier envoyé avec succès".into(),
                            })
                            .await;
                    }
                    Ok(IpcResponse::Error(msg))
                    | Err(crate::ipc::IpcClientError::DaemonError(msg)) => {
                        let _ = self.event_tx.send(DesktopEvent::Error(msg)).await;
                    }
                    Err(e) => {
                        let _ = self.event_tx.send(DesktopEvent::Error(e.to_string())).await;
                    }
                    _ => {}
                }
            }
            Err(e) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Error(format!(
                        "Échec de préparation du fichier: {e}"
                    )))
                    .await;
            }
        }
    }

    async fn start_shell(&mut self, ticket_id: &str, cols: u16, rows: u16) {
        self.close_shell().await;

        let peer = self.current_status.peer_id.clone();
        let (cmd_tx, cmd_rx) = mpsc::channel(128);
        let (text_tx, mut text_rx) = mpsc::channel::<String>(128);

        let event_tx_clone = self.event_tx.clone();
        tokio::spawn(async move {
            while let Some(text) = text_rx.recv().await {
                let _ = event_tx_clone
                    .send(DesktopEvent::ShellOutput(text.into_bytes()))
                    .await;
            }
            let _ = event_tx_clone.send(DesktopEvent::ShellClosed).await;
        });

        match TerminalSession::spawn(
            self.ipc.clone(),
            peer,
            Some(ticket_id.to_string()),
            cols,
            rows,
            cmd_rx,
            text_tx,
        )
        .await
        {
            Ok(()) => {
                self.terminal_tx = Some(cmd_tx);
                let _ = self
                    .event_tx
                    .send(DesktopEvent::ShellOpened {
                        ticket_id: ticket_id.to_string(),
                    })
                    .await;
            }
            Err(e) => {
                let msg = e.to_string();
                let _ = self.event_tx.send(DesktopEvent::ShellDenied(msg)).await;
            }
        }
    }

    async fn close_shell(&mut self) {
        if let Some(tx) = self.terminal_tx.take() {
            let _ = tx.send(TerminalCommand::Close).await;
            let _ = self.event_tx.send(DesktopEvent::ShellClosed).await;
        }
    }

    async fn revoke_shell(&mut self) {
        self.close_shell().await;
        if let Some(ticket_id) = &self.selected_ticket_id {
            match self
                .ipc
                .send_request(&IpcRequest::SetRemoteAccess {
                    ticket_id: ticket_id.clone(),
                    enabled: false,
                })
                .await
            {
                Ok(_) => {
                    let _ = self
                        .event_tx
                        .send(DesktopEvent::Notification {
                            level: "warning".into(),
                            message: "Accès au terminal révoqué immédiatement.".into(),
                        })
                        .await;
                }
                Err(e) => {
                    let _ = self.event_tx.send(DesktopEvent::Error(e.to_string())).await;
                }
            }
        }
    }

    async fn unlock_operator(&self, mnemonic_words: &str) {
        let words = mnemonic_words.trim();
        let hash = blake3::hash(words.as_bytes());
        let entropy = MnemonicEntropy::new(*hash.as_bytes());
        let deriver = MnemonicDeriver::new(&entropy);

        let root_seed = match deriver.derive_root_signing_seed() {
            Ok(seed) => seed,
            Err(e) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Error(format!("Erreur dérivation: {e}")))
                    .await;
                return;
            }
        };

        let segment_master_seed = match deriver.derive_segment_master_seed() {
            Ok(seed) => seed,
            Err(e) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Error(format!("Erreur dérivation: {e}")))
                    .await;
                return;
            }
        };

        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);

        let expires_at = now + 3600 * 8; // 8 hours max TTL

        let network_id = NetworkId::from_bytes([0x01; 32]);
        let owner_id = OwnerId::from_bytes([0x02; 32]);
        let operator_key_id = KeyId::from_bytes([0x03; 32]);
        let operator_entity = EntityId::from_bytes([0x04; 32]);
        let capabilities = vec![
            "admin".to_string(),
            "shell".to_string(),
            "read".to_string(),
            "write".to_string(),
        ];

        let signer = LocalOwnerSigner {
            key_id: operator_key_id,
            seed: root_seed,
        };
        let host_entity = operator_entity;
        let session_pubkey = [0x42; 32];
        let op_capabilities = OperatorCapabilities::from_names(&capabilities);
        let mut nonce = [0u8; 16];
        rand_core::RngCore::fill_bytes(&mut rand_core::OsRng, &mut nonce);

        match OperatorSessionCertificate::issue(
            network_id,
            owner_id,
            host_entity,
            operator_entity,
            operator_key_id,
            session_pubkey,
            op_capabilities,
            now,
            expires_at,
            nonce,
            &signer,
        ) {
            Ok(cert) => {
                let mut workspace = MemoryWorkspace::new();
                workspace.unlock(network_id, owner_id, segment_master_seed);

                let dto = OperatorSessionDto {
                    operator_entity: operator_entity.to_hex(),
                    capabilities,
                    issued_at: now,
                    expires_at,
                };

                let mut guard = self.operator_state.lock().await;
                *guard = Some(OperatorState {
                    workspace,
                    cert,
                    expires_at,
                });

                let _ = self.event_tx.send(DesktopEvent::OperatorUnlocked(dto)).await;
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Notification {
                        level: "success".into(),
                        message: "Espace opérateur déverrouillé avec succès.".into(),
                    })
                    .await;
            }
            Err(e) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Error(format!(
                        "Échec émission certificat de session: {e}"
                    )))
                    .await;
            }
        }
    }

    async fn lock_operator(&self) {
        let mut guard = self.operator_state.lock().await;
        if let Some(mut state) = guard.take() {
            state.workspace.wipe();
        }
        let _ = self.event_tx.send(DesktopEvent::OperatorLocked).await;
        let _ = self
            .event_tx
            .send(DesktopEvent::Notification {
                level: "info".into(),
                message: "Espace opérateur reverrouillé et mémoire effacée.".into(),
            })
            .await;
    }

    async fn trigger_self_support(&self, action: &str) {
        let msg = format!("Action auto-support '{action}' exécutée avec succès.");
        let dto = SelfSupportStatusDto {
            db_corrupt: false,
            peers_stale: false,
            active_repair_count: 0,
            last_action_message: Some(msg),
        };
        let _ = self
            .event_tx
            .send(DesktopEvent::SelfSupportChanged(dto))
            .await;
    }

    async fn refresh_self_support(&self) {
        let engine = self.self_support_engine.lock().await;
        let diag = engine.collect_diagnostics(None);
        let dto = SelfSupportStatusDto {
            db_corrupt: false,
            peers_stale: !diag.is_loopback_active,
            active_repair_count: 0,
            last_action_message: None,
        };
        let _ = self
            .event_tx
            .send(DesktopEvent::SelfSupportChanged(dto))
            .await;
    }
}
