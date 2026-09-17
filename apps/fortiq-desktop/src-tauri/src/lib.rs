#![allow(deprecated)]

use fs2::FileExt;
use serde::{Deserialize, Serialize};
use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, WindowEvent,
};

fn get_log_path() -> std::path::PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(std::path::PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| std::path::PathBuf::from(h).join(".fortiq")))
        .unwrap_or_else(|| std::path::PathBuf::from("."));
    base.join("FORTIQ").join("desktop.log")
}

struct DesktopInstanceLock {
    _file: std::fs::File,
}

fn acquire_desktop_instance_lock() -> Result<DesktopInstanceLock, String> {
    let path = get_log_path().with_file_name("fortiq-desktop.lock");
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|error| format!("Impossible de créer le dossier FORTIQ: {error}"))?;
    }
    let file = std::fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(&path)
        .map_err(|error| format!("Impossible d'ouvrir le verrou Desktop: {error}"))?;
    file.try_lock_exclusive().map_err(|_| {
        "Une instance de FORTIQ Desktop est déjà ouverte sur ce système".to_string()
    })?;
    Ok(DesktopInstanceLock { _file: file })
}

pub fn log_diagnostic(msg: &str) {
    let now = std::time::SystemTime::now();
    let dur = now
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = dur.as_secs();
    let millis = dur.subsec_millis();
    let line = format!("[{secs}.{millis:03}] {msg}\n");
    print!("{line}");
    let path = get_log_path();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
    {
        use std::io::Write;
        let _ = f.write_all(line.as_bytes());
        let _ = f.flush();
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopStatus {
    pub product: String,
    pub version: String,
    pub agent_state: String,
    pub mode: String,
    pub peer_id: String,
    pub active_ticket_id: Option<String>,
    pub active_ticket_state: Option<String>,
    pub authorized_operator: Option<String>,
}

const MAX_IPC_LINE_BYTES: usize = 64 * 1024;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopPeer {
    pub peer_id: String,
    pub hostname: String,
    pub os: String,
    pub transport: String,
    pub status: String,
    pub mode: Option<String>,
    pub authorized_operator: Option<String>,
    pub relay: bool,
    pub rendezvous: bool,
}

impl From<fortiq_core::ipc::PeerSummary> for DesktopPeer {
    fn from(p: fortiq_core::ipc::PeerSummary) -> Self {
        Self {
            peer_id: p.peer_id,
            hostname: p.hostname,
            os: p.os,
            transport: p.transport,
            status: p.status,
            mode: p.mode.map(|mode| mode.to_string()),
            authorized_operator: p.authorized_operator,
            relay: p.relay,
            rendezvous: p.rendezvous,
        }
    }
}

async fn send_ipc_request(
    req: &fortiq_core::ipc::IpcRequest,
) -> Option<fortiq_core::ipc::IpcResponse> {
    #[cfg(windows)]
    {
        use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
        use tokio::net::windows::named_pipe::ClientOptions;

        let pipe_name = fortiq_core::ipc::windows_pipe_name();
        let client = ClientOptions::new().open(&pipe_name).ok()?;
        let (read_half, mut write_half) = tokio::io::split(client);
        let mut reader = BufReader::new(read_half);

        let mut req_bytes = serde_json::to_vec(req).ok()?;
        req_bytes.push(b'\n');
        write_half.write_all(&req_bytes).await.ok()?;
        write_half.flush().await.ok()?;

        let mut line = String::new();
        let bytes_read = {
            let mut limiter = (&mut reader).take((MAX_IPC_LINE_BYTES + 1) as u64);
            limiter.read_line(&mut line).await.ok()?
        };
        if bytes_read > 0 && bytes_read <= MAX_IPC_LINE_BYTES {
            serde_json::from_str::<fortiq_core::ipc::IpcResponse>(line.trim()).ok()
        } else {
            None
        }
    }
    #[cfg(unix)]
    {
        use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
        use tokio::net::UnixStream;

        let path = std::env::var("FORTIQ_SOCK")
            .unwrap_or_else(|_| fortiq_core::ipc::DEFAULT_UNIX_SOCKET_PATH.to_owned());
        let stream = UnixStream::connect(&path).await.ok()?;
        let (read_half, mut write_half) = tokio::io::split(stream);
        let mut reader = BufReader::new(read_half);

        let mut req_bytes = serde_json::to_vec(req).ok()?;
        req_bytes.push(b'\n');
        write_half.write_all(&req_bytes).await.ok()?;
        write_half.flush().await.ok()?;

        let mut line = String::new();
        let bytes_read = {
            let mut limiter = (&mut reader).take((MAX_IPC_LINE_BYTES + 1) as u64);
            limiter.read_line(&mut line).await.ok()?
        };
        if bytes_read > 0 && bytes_read <= MAX_IPC_LINE_BYTES {
            serde_json::from_str::<fortiq_core::ipc::IpcResponse>(line.trim()).ok()
        } else {
            None
        }
    }
    #[cfg(not(any(windows, unix)))]
    {
        let _ = req;
        None
    }
}

#[tauri::command]
async fn desktop_status() -> DesktopStatus {
    log_diagnostic("[FRONTEND] desktop_status IPC invoked");
    if let Some(fortiq_core::ipc::IpcResponse::Status(status)) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::GetStatus).await
    {
        return DesktopStatus {
            product: status.product,
            version: status.version,
            agent_state: status.agent_state,
            mode: match status.mode {
                fortiq_core::NodeMode::Operator => "operator".to_string(),
                fortiq_core::NodeMode::Managed => "managed".to_string(),
            },
            peer_id: status.peer_id,
            active_ticket_id: status.active_ticket.as_ref().map(|t| t.id.to_string()),
            active_ticket_state: status
                .active_ticket
                .as_ref()
                .map(|t| format!("{:?}", t.state).to_uppercase()),
            authorized_operator: status.authorized_operator,
        };
    }

    // Explicit offline state when daemon is not reachable
    DesktopStatus {
        product: "FORTIQ".to_string(),
        version: env!("CARGO_PKG_VERSION").to_string(),
        agent_state: "offline".to_string(),
        mode: "offline".to_string(),
        peer_id: String::new(),
        active_ticket_id: None,
        active_ticket_state: None,
        authorized_operator: None,
    }
}

#[tauri::command]
async fn list_peers() -> Result<Vec<DesktopPeer>, String> {
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::ListPeers).await {
        match resp {
            fortiq_core::ipc::IpcResponse::Peers(peers) => {
                Ok(peers.into_iter().map(DesktopPeer::from).collect())
            }
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible (démon hors-ligne)".to_string())
    }
}

#[tauri::command]
async fn list_tickets(
    state_filter: Option<String>,
) -> Result<Vec<fortiq_core::TicketRecord>, String> {
    let filter = state_filter.and_then(|s| fortiq_core::TicketState::parse_str(&s.to_uppercase()));
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::ListTickets {
        state_filter: filter,
    })
    .await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::Tickets(tickets) => Ok(tickets),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn get_ticket(ticket_id: String) -> Result<Option<fortiq_core::TicketDetail>, String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::GetTicket { ticket_id }).await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::TicketDetail(detail) => Ok(detail),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn create_ticket(
    title: String,
    description: String,
    priority: String,
) -> Result<fortiq_core::TicketRecord, String> {
    let prio = fortiq_core::TicketPriority::parse_str(&priority.to_uppercase());
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::CreateTicket {
        title,
        description,
        priority: prio,
    })
    .await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::TicketCreated(record) => Ok(record),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn send_chat_message(
    ticket_id: String,
    body: String,
) -> Result<fortiq_core::ChatMessage, String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::SendChatMessage { ticket_id, body }).await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::MessageSent(msg) => Ok(msg),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn list_messages(ticket_id: String) -> Result<Vec<fortiq_core::ChatMessage>, String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::ListMessages { ticket_id }).await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::Messages(msgs) => Ok(msgs),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn send_file(
    ticket_id: String,
    file_path: String,
) -> Result<fortiq_core::AttachmentRecord, String> {
    let source = std::path::Path::new(&file_path);
    let staged_path = fortiq_core::ipc::new_upload_staging_path(source)
        .map_err(|error| format!("Impossible de créer le staging: {error}"))?;
    tokio::fs::copy(source, &staged_path)
        .await
        .map_err(|error| format!("Impossible de lire le fichier sélectionné: {error}"))?;
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::SendFile {
        ticket_id,
        staged_path: staged_path.to_string_lossy().to_string(),
    })
    .await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::FileSent(att) => Ok(att),
            fortiq_core::ipc::IpcResponse::Error(err) => {
                let _ = tokio::fs::remove_file(&staged_path).await;
                Err(err)
            }
            _ => {
                let _ = tokio::fs::remove_file(&staged_path).await;
                Err("Réponse inattendue du démon".to_string())
            }
        }
    } else {
        let _ = tokio::fs::remove_file(&staged_path).await;
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn list_attachments(ticket_id: String) -> Result<Vec<fortiq_core::AttachmentRecord>, String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::ListAttachments { ticket_id }).await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::Attachments(atts) => Ok(atts),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn set_remote_access(
    ticket_id: String,
    enabled: bool,
) -> Result<Option<fortiq_core::TicketRecord>, String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::SetRemoteAccess { ticket_id, enabled })
            .await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::TicketUpdated(rec) => Ok(rec),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

#[tauri::command]
async fn update_ticket_status(
    ticket_id: String,
    state: String,
) -> Result<Option<fortiq_core::TicketRecord>, String> {
    let s = fortiq_core::TicketState::parse_str(&state.to_uppercase())
        .ok_or_else(|| format!("Statut invalide: {state}"))?;
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::UpdateTicketStatus {
        ticket_id,
        state: s,
    })
    .await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::TicketUpdated(rec) => Ok(rec),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible".to_string())
    }
}

/// A live terminal session: the frame sender plus the handles of the two tasks
/// that own the IPC pipe. Both must be aborted to close the pipe, which is what
/// makes the daemon drop the P2P stream and the remote host release its
/// single-session slot.
pub struct TerminalSession {
    pub sender: tokio::sync::mpsc::Sender<fortiq_shell::ShellFrame>,
    pub writer: tokio::task::JoinHandle<()>,
    pub reader: tokio::task::JoinHandle<()>,
}

impl TerminalSession {
    async fn shutdown(self) {
        self.writer.abort();
        self.reader.abort();
        // Await cancellation so both IPC halves are actually dropped before a
        // replacement session is requested. Merely calling abort leaves a
        // short race where the daemon and remote peer still see the old shell.
        let _ = self.writer.await;
        let _ = self.reader.await;
    }
}

pub struct TerminalState(pub tokio::sync::Mutex<Option<TerminalSession>>);

#[tauri::command]
async fn start_terminal_session(
    app: tauri::AppHandle,
    state: tauri::State<'_, TerminalState>,
    peer: String,
    ticket_id: Option<String>,
    cols: u16,
    rows: u16,
) -> Result<(), String> {
    use tauri::Emitter;
    use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

    // Serialize the complete switch. Most importantly, close the old IPC/P2P
    // stream *before* asking the next remote peer for a shell. The previous
    // ordering performed the new handshake first, so any failure left the old
    // shell alive and later attempts were rejected as DENIED_BUSY.
    let mut session_guard = state.0.lock().await;
    if let Some(previous) = session_guard.take() {
        previous.shutdown().await;
        // The managed host performs bounded PTY cleanup after observing EOF.
        // Give that cleanup time to release its single-session guard.
        tokio::time::sleep(std::time::Duration::from_millis(750)).await;
    }

    #[cfg(windows)]
    let stream = {
        use tokio::net::windows::named_pipe::ClientOptions;
        let pipe_name = fortiq_core::ipc::windows_terminal_pipe_name();
        ClientOptions::new().open(&pipe_name).map_err(|e| {
            format!("Impossible de se connecter au pipe terminal ({pipe_name}): {e}")
        })?
    };

    #[cfg(unix)]
    let stream = {
        use tokio::net::UnixStream;
        let path = fortiq_core::ipc::unix_terminal_socket_path();
        UnixStream::connect(&path)
            .await
            .map_err(|e| format!("Impossible de se connecter au socket terminal ({path}): {e}"))?
    };

    #[cfg(not(any(windows, unix)))]
    return Err("Plateforme non supportée".to_string());

    let (read_half, mut write_half) = tokio::io::split(stream);

    let init = fortiq_core::ipc::TerminalSessionInit {
        peer,
        ticket_id,
        cols,
        rows,
        dial: None,
    };
    let mut init_bytes = serde_json::to_vec(&init).map_err(|e| e.to_string())?;
    init_bytes.push(b'\n');
    write_half
        .write_all(&init_bytes)
        .await
        .map_err(|e| e.to_string())?;
    write_half.flush().await.map_err(|e| e.to_string())?;

    let mut reader = BufReader::new(read_half);
    let mut status_line = String::new();
    reader
        .read_line(&mut status_line)
        .await
        .map_err(|e| format!("Échec de lecture du handshake: {e}"))?;

    #[derive(serde::Deserialize)]
    struct HandshakeResp {
        status: String,
        message: Option<String>,
    }

    let resp: HandshakeResp = serde_json::from_str(status_line.trim())
        .map_err(|e| format!("Réponse handshake invalide: {e}"))?;

    if resp.status != "ok" {
        return Err(resp
            .message
            .unwrap_or_else(|| "Connexion terminal refusée par le démon".to_string()));
    }

    let (tx, mut rx) = tokio::sync::mpsc::channel::<fortiq_shell::ShellFrame>(128);

    let writer = tokio::spawn(async move {
        while let Some(frame) = rx.recv().await {
            if frame.write_to(&mut write_half).await.is_err() {
                break;
            }
        }
    });

    let mut read_half = reader.into_inner();
    let app_clone = app.clone();
    let pong_tx = tx.clone();
    let session_reader = tokio::spawn(async move {
        loop {
            match fortiq_shell::ShellFrame::read_from(&mut read_half).await {
                Ok(Some(fortiq_shell::ShellFrame::Data(bytes))) => {
                    let text = String::from_utf8_lossy(&bytes).to_string();
                    let _ = app_clone.emit("terminal-output", text);
                }
                Ok(Some(fortiq_shell::ShellFrame::Ping)) => {
                    // Answer the host's keepalive, otherwise it treats this
                    // console as gone and ends the session.
                    if pong_tx.send(fortiq_shell::ShellFrame::Pong).await.is_err() {
                        break;
                    }
                }
                Ok(Some(fortiq_shell::ShellFrame::Pong)) => {}
                Ok(Some(fortiq_shell::ShellFrame::Resize { .. })) => {}
                Ok(None) => break,
                Err(_) => break,
            }
        }
        let _ = app_clone.emit("terminal-closed", ());
    });

    *session_guard = Some(TerminalSession {
        sender: tx,
        writer,
        reader: session_reader,
    });

    Ok(())
}

#[tauri::command]
async fn write_terminal_data(
    state: tauri::State<'_, TerminalState>,
    data: String,
) -> Result<(), String> {
    let guard = state.0.lock().await;
    if let Some(session) = guard.as_ref() {
        session
            .sender
            .send(fortiq_shell::ShellFrame::Data(data.into_bytes()))
            .await
            .map_err(|e| format!("Échec d'envoi des données terminal: {e}"))?;
        Ok(())
    } else {
        Err("Aucune session terminal active".to_string())
    }
}

#[tauri::command]
async fn resize_terminal(
    state: tauri::State<'_, TerminalState>,
    cols: u16,
    rows: u16,
) -> Result<(), String> {
    let guard = state.0.lock().await;
    if let Some(session) = guard.as_ref() {
        session
            .sender
            .send(fortiq_shell::ShellFrame::Resize { cols, rows })
            .await
            .map_err(|e| format!("Échec d'envoi du redimensionnement: {e}"))?;
        Ok(())
    } else {
        Ok(())
    }
}

#[tauri::command]
async fn close_terminal_session(state: tauri::State<'_, TerminalState>) -> Result<(), String> {
    let mut guard = state.0.lock().await;
    if let Some(session) = guard.take() {
        session.shutdown().await;
    }
    Ok(())
}

// -----------------------------------------------------------------------------
// Canonical Architecture v3 Subsystem State & Commands
// -----------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SelfSupportDiagnosticsDto {
    pub os: String,
    pub arch: String,
    pub hostname: String,
    pub is_loopback_active: bool,
    pub relay_bypassed: bool,
    pub active_shards: usize,
    pub event_packs_stored: usize,
    pub canonical_heads: usize,
    pub timestamp_secs: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SelfSupportTicketDto {
    pub ticket_id: String,
    pub title: String,
    pub description: String,
    pub created_at: u64,
    pub access_epoch: String,
    pub is_closed: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OperatorSessionDto {
    pub operator_entity: String,
    pub capabilities: Vec<String>,
    pub issued_at: u64,
    pub expires_at: u64,
}

pub struct PortableOperatorSession {
    pub workspace: fortiq_core::canonical::portable::workspace::MemoryWorkspace,
    pub cert: fortiq_core::canonical::portable::certificate::OperatorSessionCertificate,
    pub expires_at: u64,
}

pub struct CanonicalDesktopState {
    pub self_support_engine:
        tokio::sync::Mutex<fortiq_core::canonical::self_support::SelfSupportEngine>,
    pub portable_operator: tokio::sync::Mutex<Option<PortableOperatorSession>>,
}

pub struct MockOwnerSigner {
    pub key_id: fortiq_core::canonical::types::KeyId,
    pub seed: fortiq_core::canonical::crypto::keys::OwnerRootSigningSeed,
}

impl fortiq_core::canonical::signing::Signer for MockOwnerSigner {
    fn sign(
        &self,
        domain_separated_data: &[u8],
    ) -> Result<Vec<u8>, fortiq_core::canonical::signing::SigningError> {
        let mut hasher = blake3::Hasher::new_keyed(self.seed.as_bytes());
        hasher.update(domain_separated_data);
        Ok(hasher.finalize().as_bytes().to_vec())
    }

    fn key_id(&self) -> fortiq_core::canonical::types::KeyId {
        self.key_id
    }
}

impl CanonicalDesktopState {
    pub fn new_default() -> Self {
        let local_device_id = fortiq_core::canonical::types::EntityId::from_bytes([0x42; 32]);
        let this_device = fortiq_core::canonical::self_support::ThisDevice::new(
            local_device_id,
            fortiq_core::canonical::self_support::LoopbackEndpoint::default(),
        );
        let self_support_engine =
            fortiq_core::canonical::self_support::SelfSupportEngine::new(this_device);
        Self {
            self_support_engine: tokio::sync::Mutex::new(self_support_engine),
            portable_operator: tokio::sync::Mutex::new(None),
        }
    }

    pub async fn get_diagnostics(&self) -> SelfSupportDiagnosticsDto {
        let engine = self.self_support_engine.lock().await;
        let storage_summary = fortiq_core::canonical::self_support::StorageDiagnostics {
            active_shards: 12,
            event_packs_stored: 24,
            canonical_heads: 1,
            local_storage_bytes: 1024 * 512,
        };
        let diag = engine.collect_diagnostics(Some(storage_summary));
        SelfSupportDiagnosticsDto {
            os: diag.os,
            arch: diag.arch,
            hostname: diag.hostname,
            is_loopback_active: diag.is_loopback_active,
            relay_bypassed: diag.relay_bypassed,
            active_shards: diag.storage.active_shards,
            event_packs_stored: diag.storage.event_packs_stored,
            canonical_heads: diag.storage.canonical_heads,
            timestamp_secs: diag.timestamp_secs,
        }
    }

    pub async fn create_ticket(
        &self,
        title: String,
        description: String,
    ) -> Result<SelfSupportTicketDto, String> {
        let mut engine = self.self_support_engine.lock().await;
        let creator_id = *engine.this_device().device_id();
        let ticket = engine
            .create_self_support_ticket(title, description, creator_id)
            .map_err(|e| format!("Erreur création ticket auto-support: {e}"))?;

        Ok(SelfSupportTicketDto {
            ticket_id: ticket.ticket_id.to_hex(),
            title: ticket.title,
            description: ticket.description,
            created_at: ticket.created_at,
            access_epoch: hex::encode(ticket.current_epoch.as_bytes()),
            is_closed: ticket.is_closed,
        })
    }

    pub async fn unlock_operator(
        &self,
        mnemonic_words: &str,
    ) -> Result<OperatorSessionDto, String> {
        let words = mnemonic_words.trim();
        let hash = blake3::hash(words.as_bytes());
        let entropy = fortiq_core::canonical::crypto::keys::MnemonicEntropy::new(*hash.as_bytes());
        let deriver = fortiq_core::canonical::portable::mnemonic::MnemonicDeriver::new(&entropy);

        let root_seed = deriver
            .derive_root_signing_seed()
            .map_err(|e| format!("Échec dérivation graine racine: {e}"))?;
        let segment_master_seed = deriver
            .derive_segment_master_seed()
            .map_err(|e| format!("Échec dérivation graine segment: {e}"))?;

        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);

        let expires_at = now + 3600;

        let network_id = fortiq_core::canonical::types::NetworkId::from_bytes([0x01; 32]);
        let owner_id = fortiq_core::canonical::types::OwnerId::from_bytes([0x02; 32]);
        let operator_key_id = fortiq_core::canonical::types::KeyId::from_bytes([0x03; 32]);
        let operator_entity = fortiq_core::canonical::types::EntityId::from_bytes([0x04; 32]);
        let capabilities = vec![
            "admin".to_string(),
            "shell".to_string(),
            "read".to_string(),
            "write".to_string(),
        ];

        let signer = MockOwnerSigner {
            key_id: operator_key_id,
            seed: root_seed,
        };
        let cert =
            fortiq_core::canonical::portable::certificate::OperatorSessionCertificate::issue(
                network_id,
                owner_id,
                operator_key_id,
                operator_entity,
                capabilities.clone(),
                now,
                expires_at,
                &signer,
            )
            .map_err(|e| format!("Échec émission certificat de session: {e}"))?;

        let mut workspace = fortiq_core::canonical::portable::workspace::MemoryWorkspace::new();
        workspace.unlock(network_id, owner_id, segment_master_seed);

        let dto = OperatorSessionDto {
            operator_entity: operator_entity.to_hex(),
            capabilities,
            issued_at: now,
            expires_at,
        };

        let mut guard = self.portable_operator.lock().await;
        *guard = Some(PortableOperatorSession {
            workspace,
            cert,
            expires_at,
        });

        Ok(dto)
    }

    pub async fn lock_operator(&self) {
        let mut guard = self.portable_operator.lock().await;
        if let Some(mut session) = guard.take() {
            session.workspace.wipe();
        }
    }

    pub async fn is_operator_unlocked(&self) -> bool {
        let guard = self.portable_operator.lock().await;
        guard
            .as_ref()
            .map(|s| s.workspace.is_unlocked())
            .unwrap_or(false)
    }
}

#[tauri::command]
async fn get_self_support_diagnostics(
    state: tauri::State<'_, CanonicalDesktopState>,
) -> Result<SelfSupportDiagnosticsDto, String> {
    log_diagnostic("[CANONICAL] get_self_support_diagnostics invoked");
    Ok(state.get_diagnostics().await)
}

#[tauri::command]
async fn create_self_support_ticket(
    state: tauri::State<'_, CanonicalDesktopState>,
    title: String,
    description: String,
) -> Result<SelfSupportTicketDto, String> {
    log_diagnostic(&format!("[CANONICAL] create_self_support_ticket: {title}"));
    state.create_ticket(title, description).await
}

#[tauri::command]
async fn unlock_portable_operator(
    state: tauri::State<'_, CanonicalDesktopState>,
    mnemonic_words: String,
) -> Result<OperatorSessionDto, String> {
    log_diagnostic("[CANONICAL] unlock_portable_operator invoked");
    let res = state.unlock_operator(&mnemonic_words).await;
    if res.is_ok() {
        log_diagnostic("[CANONICAL] Portable operator session unlocked successfully");
    }
    res
}

#[tauri::command]
async fn lock_portable_operator(
    state: tauri::State<'_, CanonicalDesktopState>,
) -> Result<(), String> {
    log_diagnostic("[CANONICAL] lock_portable_operator invoked -> wiping session memory");
    state.lock_operator().await;
    Ok(())
}

impl TerminalState {
    pub async fn revoke_session(&self) {
        let mut guard = self.0.lock().await;
        if let Some(session) = guard.take() {
            session.shutdown().await;
            log_diagnostic("[CANONICAL] Terminal session aborted immediately via shutdown handle");
        }
    }
}

#[tauri::command]
async fn revoke_active_shell(
    terminal_state: tauri::State<'_, TerminalState>,
    ticket_id: String,
) -> Result<(), String> {
    log_diagnostic(&format!(
        "[CANONICAL] Emergency revocation requested for ticket {ticket_id}"
    ));
    terminal_state.revoke_session().await;
    Ok(())
}

fn show_main_window(app: &tauri::AppHandle) {
    log_diagnostic("[ACTION] show_main_window triggered");
    if let Some(window) = app.get_webview_window("main") {
        match window.show() {
            Ok(_) => log_diagnostic("[ACTION] window.show() success"),
            Err(e) => log_diagnostic(&format!("[ACTION ERROR] window.show() failed: {e}")),
        }
        let _ = window.unminimize();
        let _ = window.set_focus();
        log_diagnostic("[ACTION] window unminimized and focused");
    } else {
        log_diagnostic("[ACTION ERROR] 'main' window not found!");
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    log_diagnostic("=== FORTIQ Desktop starting ===");
    log_diagnostic(&format!("Log file path: {}", get_log_path().display()));

    let instance_lock = match acquire_desktop_instance_lock() {
        Ok(lock) => lock,
        Err(error) => {
            log_diagnostic(&format!("[STARTUP BLOCKED] {error}"));
            return;
        }
    };

    let canonical_state = CanonicalDesktopState::new_default();

    tauri::Builder::default()
        .manage(instance_lock)
        .manage(TerminalState(tokio::sync::Mutex::new(None)))
        .manage(canonical_state)
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            log_diagnostic("[SETUP] Beginning application setup...");

            let open = MenuItem::with_id(app, "open", "Ouvrir FORTIQ", true, None::<&str>)?;
            let status = MenuItem::with_id(app, "status", "Statut : Prêt", false, None::<&str>)?;
            let separator = PredefinedMenuItem::separator(app)?;
            let quit = MenuItem::with_id(app, "quit", "Quitter FORTIQ", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&open, &status, &separator, &quit])?;
            log_diagnostic("[SETUP] Tray menu created");

            let mut tray = TrayIconBuilder::with_id("fortiq")
                .tooltip("FORTIQ · Supervision P2P Souveraine")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "open" => {
                        log_diagnostic("[TRAY] Menu event 'open' clicked");
                        show_main_window(app);
                    }
                    "quit" => {
                        log_diagnostic("[TRAY] Menu event 'quit' clicked, exiting app");
                        app.exit(0);
                    }
                    other => {
                        log_diagnostic(&format!("[TRAY] Menu event '{other}' clicked"));
                    }
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        log_diagnostic("[TRAY] Tray icon Left-Click detected, showing window");
                        show_main_window(tray.app_handle());
                    }
                });

            let icon = match app.default_window_icon() {
                Some(icon) => {
                    log_diagnostic("[SETUP] Using default_window_icon for tray");
                    icon.clone()
                }
                None => {
                    log_diagnostic(
                        "[SETUP] default_window_icon is None, using embedded 32x32 icon",
                    );
                    tauri::include_image!("icons/32x32.png")
                }
            };
            tray = tray.icon(icon);

            log_diagnostic("[SETUP] Building tray icon...");
            match tray.build(app) {
                Ok(_) => {
                    log_diagnostic("[SETUP] Tray icon registered successfully in Windows tray!");
                }
                Err(e) => {
                    log_diagnostic(&format!("[SETUP ERROR] Failed to build tray icon: {e}"));
                    eprintln!("FATAL: Failed to build tray icon: {e}");
                    return Err(Box::new(e));
                }
            }

            log_diagnostic("[SETUP] Querying 'main' WebviewWindow...");
            match app.get_webview_window("main") {
                Some(window) => {
                    log_diagnostic("[SETUP] 'main' window found in app registry");
                    match window.show() {
                        Ok(_) => log_diagnostic("[SETUP] Initial window.show() succeeded"),
                        Err(e) => {
                            log_diagnostic(&format!("[SETUP ERROR] window.show() failed: {e}"))
                        }
                    }
                    let _ = window.unminimize();
                    let _ = window.set_focus();
                }
                None => {
                    log_diagnostic("[SETUP ERROR] 'main' window NOT FOUND in app registry!");
                }
            }

            log_diagnostic("[SETUP] Setup completed successfully");
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                log_diagnostic(
                    "[WINDOW] CloseRequested received -> preventing close, hiding to tray",
                );
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            desktop_status,
            list_peers,
            list_tickets,
            get_ticket,
            create_ticket,
            send_chat_message,
            list_messages,
            send_file,
            list_attachments,
            set_remote_access,
            update_ticket_status,
            start_terminal_session,
            write_terminal_data,
            resize_terminal,
            close_terminal_session,
            get_self_support_diagnostics,
            create_self_support_ticket,
            unlock_portable_operator,
            lock_portable_operator,
            revoke_active_shell
        ])
        .run(tauri::generate_context!())
        .expect("error while running FORTIQ desktop");
}
