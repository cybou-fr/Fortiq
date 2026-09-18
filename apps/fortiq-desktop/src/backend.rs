use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, Mutex};
use tracing::warn;

use fortiq_core::canonical::self_support::{LoopbackEndpoint, SelfSupportEngine, ThisDevice};
use fortiq_core::canonical::types::EntityId;
use fortiq_core::ipc::{IpcRequest, IpcResponse};

use crate::command::DesktopCommand;
use crate::event::DesktopEvent;
use crate::ipc::IpcClient;
use crate::models::{
    AttachmentDto, ChatMessageDto, DesktopPeerDto, DesktopStatusDto, OperatorSessionDto,
    SelfSupportStatusDto, TicketDetailDto, TicketSummaryDto,
};
use crate::terminal::{TerminalCommand, TerminalSession};

pub struct BackendActor {
    ipc: Arc<IpcClient>,
    cmd_rx: mpsc::Receiver<DesktopCommand>,
    event_tx: mpsc::Sender<DesktopEvent>,
    selected_ticket_id: Option<String>,
    selected_ticket_detail: Option<TicketDetailDto>,
    current_status: DesktopStatusDto,
    terminal_tx: Option<mpsc::Sender<TerminalCommand>>,
    self_support_engine: Arc<Mutex<SelfSupportEngine>>,
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
            selected_ticket_detail: None,
            current_status: DesktopStatusDto::default(),
            terminal_tx: None,
            self_support_engine: Arc::new(Mutex::new(engine)),
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
                let is_unlocked = status.is_operator_unlocked;
                let status_dto = DesktopStatusDto {
                    product: status.product,
                    version: status.version,
                    agent_state: status.agent_state,
                    peer_id: status.peer_id,
                    active_ticket_id: status.active_ticket.as_ref().map(|t| t.id.clone()),
                    active_ticket_state: status
                        .active_ticket
                        .as_ref()
                        .map(|t| format!("{:?}", t.state)),
                    is_operator_unlocked: is_unlocked,
                };
                self.current_status = status_dto.clone();
                let _ = self
                    .event_tx
                    .send(DesktopEvent::StatusChanged(status_dto))
                    .await;
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

        // Poll operator status from daemon authority
        if let Ok(IpcResponse::OperatorStatus(op_status)) =
            self.ipc.send_request(&IpcRequest::GetOperatorStatus).await
        {
            if op_status.is_unlocked {
                let dto = OperatorSessionDto {
                    operator_entity: op_status.owner_id.unwrap_or_default(),
                    capabilities: op_status.capabilities,
                    issued_at: 0,
                    expires_at: op_status.expires_at.unwrap_or_default(),
                };
                let _ = self
                    .event_tx
                    .send(DesktopEvent::OperatorUnlocked(dto))
                    .await;
            } else {
                let _ = self.event_tx.send(DesktopEvent::OperatorLocked).await;
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
                    relay: p.relay,
                    rendezvous: p.rendezvous,
                })
                .collect();
            let _ = self
                .event_tx
                .send(DesktopEvent::PeersChanged(peer_dtos))
                .await;
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

    async fn load_ticket_detail(&mut self, ticket_id: &str) {
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
                    client_peer_id: detail.ticket.client_peer_id,
                    messages: msgs,
                    attachments,
                };
                self.selected_ticket_detail = Some(dto.clone());
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

        let target_peer = match &self.selected_ticket_detail {
            Some(detail) if detail.id == ticket_id => {
                let local_peer = &self.current_status.peer_id;
                if &detail.client_peer_id == local_peer {
                    let msg =
                        "Impossible d'ouvrir un shell : le nœud local est le client du ticket"
                            .to_string();
                    let _ = self.event_tx.send(DesktopEvent::ShellDenied(msg)).await;
                    return;
                }
                detail.client_peer_id.clone()
            }
            _ => {
                match self
                    .ipc
                    .send_request(&IpcRequest::GetTicket {
                        ticket_id: ticket_id.to_string(),
                    })
                    .await
                {
                    Ok(IpcResponse::TicketDetail(Some(detail))) => {
                        let local_peer = &self.current_status.peer_id;
                        if &detail.ticket.client_peer_id == local_peer {
                            let msg = "Impossible d'ouvrir un shell : le nœud local est le client du ticket"
                                .to_string();
                            let _ = self.event_tx.send(DesktopEvent::ShellDenied(msg)).await;
                            return;
                        }
                        detail.ticket.client_peer_id
                    }
                    _ => {
                        let msg =
                            "Impossible de résoudre le pair client pour ce ticket".to_string();
                        let _ = self.event_tx.send(DesktopEvent::ShellDenied(msg)).await;
                        return;
                    }
                }
            }
        };

        if target_peer.is_empty() || target_peer == self.current_status.peer_id {
            let msg = "Pair distant invalide ou boucle locale détectée".to_string();
            let _ = self.event_tx.send(DesktopEvent::ShellDenied(msg)).await;
            return;
        }

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
            target_peer,
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

    async fn unlock_operator(&mut self, mnemonic_words: &str) {
        match self
            .ipc
            .send_request(&IpcRequest::UnlockOperator {
                mnemonic: mnemonic_words.trim().to_string(),
            })
            .await
        {
            Ok(IpcResponse::OperatorStatus(status)) if status.is_unlocked => {
                let dto = OperatorSessionDto {
                    operator_entity: status.owner_id.unwrap_or_default(),
                    capabilities: status.capabilities,
                    issued_at: 0,
                    expires_at: status.expires_at.unwrap_or_default(),
                };
                let _ = self
                    .event_tx
                    .send(DesktopEvent::OperatorUnlocked(dto))
                    .await;
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Notification {
                        level: "success".into(),
                        message: "Espace opérateur déverrouillé avec succès auprès du démon."
                            .into(),
                    })
                    .await;
                self.handle_refresh().await;
            }
            Ok(IpcResponse::Error(msg)) => {
                let _ = self.event_tx.send(DesktopEvent::Error(msg)).await;
            }
            Ok(_) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Error(
                        "Réponse inattendue du démon lors du déverrouillage".into(),
                    ))
                    .await;
            }
            Err(e) => {
                let _ = self
                    .event_tx
                    .send(DesktopEvent::Error(format!("Erreur IPC: {e}")))
                    .await;
            }
        }
    }

    async fn lock_operator(&mut self) {
        let _ = self.ipc.send_request(&IpcRequest::LockOperator).await;
        let _ = self.event_tx.send(DesktopEvent::OperatorLocked).await;
        let _ = self
            .event_tx
            .send(DesktopEvent::Notification {
                level: "info".into(),
                message: "Espace opérateur reverrouillé et mémoire effacée sur le démon.".into(),
            })
            .await;
        self.handle_refresh().await;
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
