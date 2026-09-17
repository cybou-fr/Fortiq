use std::sync::Arc;

use anyhow::Result;
use fortiq_core::{
    ipc::{DaemonStatus, IpcRequest, IpcResponse},
    Config, NodeMode, TicketStore,
};
use libp2p::PeerId;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};

pub const MAX_IPC_LINE_BYTES: usize = 64 * 1024;

pub struct IpcState {
    pub config: Config,
    pub peer_id: PeerId,
    pub listen_addresses: Vec<String>,
    pub ticket_store: TicketStore,
    pub p2p_sender: Option<tokio::sync::mpsc::Sender<fortiq_p2p::P2pCommand>>,
}

pub async fn run_ipc_server(state: Arc<IpcState>) -> Result<()> {
    tokio::try_join!(
        run_command_ipc(Arc::clone(&state)),
        run_terminal_ipc(Arc::clone(&state)),
    )?;
    Ok(())
}

async fn run_command_ipc(state: Arc<IpcState>) -> Result<()> {
    #[cfg(windows)]
    {
        run_windows_pipe(state).await
    }
    #[cfg(unix)]
    {
        run_unix_socket(state).await
    }
}

async fn run_terminal_ipc(state: Arc<IpcState>) -> Result<()> {
    #[cfg(windows)]
    {
        run_windows_terminal_pipe(state).await
    }
    #[cfg(unix)]
    {
        run_unix_terminal_socket(state).await
    }
}

#[cfg(windows)]
async fn run_windows_pipe(state: Arc<IpcState>) -> Result<()> {
    let pipe_name = state.config.ipc_endpoint();
    let mode = state.config.mode();
    tracing::info!("Starting Windows Named Pipe IPC server at {}", pipe_name);

    let mut server = create_windows_pipe(&pipe_name, true, false, mode)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Named pipe connection failed: {err}");
            server = create_windows_pipe(&pipe_name, false, false, mode)?;
            continue;
        }

        let client = server;
        server = create_windows_pipe(&pipe_name, false, false, mode)?;

        let state_clone = Arc::clone(&state);
        tokio::spawn(async move {
            if let Err(e) = handle_client(client, state_clone).await {
                tracing::debug!("IPC client disconnected: {e}");
            }
        });
    }
}

#[cfg(windows)]
async fn run_windows_terminal_pipe(state: Arc<IpcState>) -> Result<()> {
    let pipe_name = state.config.terminal_ipc_endpoint();
    let mode = state.config.mode();
    tracing::info!("Starting Windows Terminal Named Pipe at {}", pipe_name);

    let mut server = create_windows_pipe(&pipe_name, true, true, mode)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Terminal named pipe connection failed: {err}");
            server = create_windows_pipe(&pipe_name, false, true, mode)?;
            continue;
        }

        let client = server;
        server = create_windows_pipe(&pipe_name, false, true, mode)?;

        let state_clone = Arc::clone(&state);
        tokio::spawn(async move {
            if let Err(e) = handle_terminal_client(client, state_clone).await {
                tracing::debug!("Terminal client disconnected: {e}");
            }
        });
    }
}

#[cfg(windows)]
fn lookup_group_sid(name: &str) -> Option<String> {
    use std::{ffi::OsStr, iter, os::windows::ffi::OsStrExt, ptr};
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::{Authorization::ConvertSidToStringSidW, LookupAccountNameW, SID_NAME_USE},
    };

    let wide: Vec<u16> = OsStr::new(name)
        .encode_wide()
        .chain(iter::once(0))
        .collect();
    let mut sid_len = 0u32;
    let mut domain_len = 0u32;
    let mut sid_use: SID_NAME_USE = 0;

    unsafe {
        LookupAccountNameW(
            ptr::null(),
            wide.as_ptr(),
            ptr::null_mut(),
            &mut sid_len,
            ptr::null_mut(),
            &mut domain_len,
            &mut sid_use,
        );
    }

    if sid_len == 0 {
        return None;
    }

    let mut sid_buf = vec![0u8; sid_len as usize];
    let mut domain_buf = vec![0u16; domain_len as usize];

    let ok = unsafe {
        LookupAccountNameW(
            ptr::null(),
            wide.as_ptr(),
            sid_buf.as_mut_ptr().cast(),
            &mut sid_len,
            domain_buf.as_mut_ptr(),
            &mut domain_len,
            &mut sid_use,
        )
    };

    if ok == 0 {
        return None;
    }

    let mut string_sid: *mut u16 = ptr::null_mut();
    let converted = unsafe { ConvertSidToStringSidW(sid_buf.as_mut_ptr().cast(), &mut string_sid) };

    if converted == 0 || string_sid.is_null() {
        return None;
    }

    let mut len = 0;
    unsafe {
        while *string_sid.add(len) != 0 {
            len += 1;
        }
        let slice = std::slice::from_raw_parts(string_sid, len);
        let result = String::from_utf16(slice).ok();
        LocalFree(string_sid.cast());
        result
    }
}

/// Creates a local-only named pipe with explicit security descriptors.
/// - Operator node (command & terminal pipes) or Terminal pipe:
///   Grants Full Control to LocalSystem (SY) and Builtin Administrators (BA).
///   Grants Read/Write to members of the local OS group 'FORTIQ Operators' (or 'FORTIQ-Operators'),
///   which is not filtered by Windows UAC and allows standard non-elevated desktop sessions to
///   interact with the service. Unprivileged users outside this group are denied.
/// - Managed node command pipe: grants read/write to Interactive Users (IU) so non-elevated
///   desktop and CLI users can open support tickets and inspect status.
#[cfg(windows)]
fn create_windows_pipe(
    pipe_name: &str,
    first_instance: bool,
    is_terminal: bool,
    mode: NodeMode,
) -> std::io::Result<tokio::net::windows::named_pipe::NamedPipeServer> {
    use std::{ffi::c_void, iter, ptr};
    use tokio::net::windows::named_pipe::ServerOptions;
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::{
            Authorization::{
                ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
            },
            SECURITY_ATTRIBUTES,
        },
    };

    let sddl_str = if is_terminal || mode == NodeMode::Operator {
        if let Some(op_sid) =
            lookup_group_sid("FORTIQ Operators").or_else(|| lookup_group_sid("FORTIQ-Operators"))
        {
            format!("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{})", op_sid)
        } else {
            tracing::warn!(
                "Local operator group 'FORTIQ Operators' not found; pipe restricted to SYSTEM and Administrators"
            );
            "D:P(A;;GA;;;SY)(A;;GA;;;BA)".to_string()
        }
    } else {
        "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)".to_string()
    };

    let sddl: Vec<u16> = sddl_str.encode_utf16().chain(iter::once(0)).collect();
    let mut descriptor: *mut c_void = ptr::null_mut();

    // SAFETY: `sddl` is NUL-terminated and remains alive for the duration of the
    // conversion. Windows allocates `descriptor`, which is released with LocalFree.
    let converted = unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.as_ptr(),
            SDDL_REVISION_1,
            &mut descriptor,
            ptr::null_mut(),
        )
    };
    if converted == 0 {
        return Err(std::io::Error::last_os_error());
    }

    let mut security_attributes = SECURITY_ATTRIBUTES {
        nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: descriptor,
        bInheritHandle: 0,
    };
    let mut options = ServerOptions::new();
    options
        .first_pipe_instance(first_instance)
        .reject_remote_clients(true);

    // SAFETY: `security_attributes` and its descriptor stay valid throughout the
    // synchronous CreateNamedPipe call. Tokio does not retain either pointer.
    let attributes_ptr = (&mut security_attributes as *mut SECURITY_ATTRIBUTES).cast::<c_void>();
    let result = unsafe { options.create_with_security_attributes_raw(pipe_name, attributes_ptr) };

    // SAFETY: the descriptor was allocated by the conversion call above and is
    // no longer needed after CreateNamedPipe has returned.
    unsafe {
        LocalFree(descriptor);
    }

    result
}

#[cfg(unix)]
fn setup_unix_socket_permissions_and_group(path: &str, mode: u32) {
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::set_permissions(path, std::fs::Permissions::from_mode(mode));

    // If system group 'fortiq' exists, assign the socket to that group (preserving owner uid)
    if let Ok(c_name) = std::ffi::CString::new("fortiq") {
        unsafe {
            let grp = libc::getgrnam(c_name.as_ptr());
            if !grp.is_null() {
                let gid = (*grp).gr_gid;
                if let Ok(c_path) = std::ffi::CString::new(path) {
                    let _ = libc::chown(c_path.as_ptr(), u32::MAX, gid);
                }
            }
        }
    }
}

#[cfg(unix)]
async fn run_unix_socket(state: Arc<IpcState>) -> Result<()> {
    use tokio::net::UnixListener;

    let path = state.config.ipc_endpoint();
    let _ = tokio::fs::remove_file(&path).await;
    if let Some(parent) = std::path::Path::new(&path).parent() {
        let _ = tokio::fs::create_dir_all(parent).await;
        #[cfg(unix)]
        if let Some(p_str) = parent.to_str() {
            let parent_mode = if state.config.mode() == NodeMode::Operator {
                0o770
            } else {
                0o755
            };
            setup_unix_socket_permissions_and_group(p_str, parent_mode);
        }
    }
    let listener = UnixListener::bind(&path)?;
    #[cfg(unix)]
    {
        let socket_mode = if state.config.mode() == NodeMode::Operator {
            0o660
        } else {
            0o666
        };
        setup_unix_socket_permissions_and_group(&path, socket_mode);
    }
    tracing::info!("Starting Unix Domain Socket IPC server at {}", path);

    loop {
        match listener.accept().await {
            Ok((stream, _)) => {
                let state_clone = Arc::clone(&state);
                tokio::spawn(async move {
                    if let Err(e) = handle_client(stream, state_clone).await {
                        tracing::debug!("IPC client disconnected: {e}");
                    }
                });
            }
            Err(e) => {
                tracing::warn!("Unix socket accept error: {e}");
                tokio::time::sleep(std::time::Duration::from_millis(100)).await;
            }
        }
    }
}

#[cfg(unix)]
async fn run_unix_terminal_socket(state: Arc<IpcState>) -> Result<()> {
    use tokio::net::UnixListener;

    let path = state.config.terminal_ipc_endpoint();
    let _ = tokio::fs::remove_file(&path).await;
    if let Some(parent) = std::path::Path::new(&path).parent() {
        let _ = tokio::fs::create_dir_all(parent).await;
        #[cfg(unix)]
        if let Some(p_str) = parent.to_str() {
            setup_unix_socket_permissions_and_group(p_str, 0o770);
        }
    }
    let listener = UnixListener::bind(&path)?;
    #[cfg(unix)]
    {
        setup_unix_socket_permissions_and_group(&path, 0o660);
    }
    tracing::info!("Starting Unix Terminal Socket at {}", path);

    loop {
        match listener.accept().await {
            Ok((stream, _)) => {
                let state_clone = Arc::clone(&state);
                tokio::spawn(async move {
                    if let Err(e) = handle_terminal_client(stream, state_clone).await {
                        tracing::debug!("Terminal client disconnected: {e}");
                    }
                });
            }
            Err(e) => {
                tracing::warn!("Terminal unix socket accept error: {e}");
                tokio::time::sleep(std::time::Duration::from_millis(100)).await;
            }
        }
    }
}

async fn handle_client<S>(stream: S, state: Arc<IpcState>) -> Result<()>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
{
    let (read_half, mut write_half) = tokio::io::split(stream);
    let mut reader = BufReader::new(read_half);
    let mut line = String::new();

    loop {
        line.clear();
        let bytes_read = {
            let mut limiter = (&mut reader).take((MAX_IPC_LINE_BYTES + 1) as u64);
            limiter.read_line(&mut line).await?
        };
        if bytes_read == 0 {
            break;
        }
        if bytes_read > MAX_IPC_LINE_BYTES {
            let response =
                IpcResponse::Error("Request exceeds maximum size limit (64 KB)".to_string());
            let mut res_bytes = serde_json::to_vec(&response)?;
            res_bytes.push(b'\n');
            write_half.write_all(&res_bytes).await?;
            write_half.flush().await?;
            break;
        }

        let trimmed = line.trim();
        if !trimmed.is_empty() {
            let response = match serde_json::from_str::<IpcRequest>(trimmed) {
                Ok(req) => process_request(req, &state).await,
                Err(err) => IpcResponse::Error(format!("Malformed IPC request: {err}")),
            };
            let mut res_bytes = serde_json::to_vec(&response)?;
            res_bytes.push(b'\n');
            write_half.write_all(&res_bytes).await?;
            write_half.flush().await?;
        }
    }

    Ok(())
}

async fn process_request(req: IpcRequest, state: &IpcState) -> IpcResponse {
    match req {
        IpcRequest::GetStatus => {
            let active_ticket = state.ticket_store.get().await.ok().flatten();
            let status = DaemonStatus {
                product: "FORTIQ".to_string(),
                version: env!("CARGO_PKG_VERSION").to_string(),
                mode: state.config.mode(),
                peer_id: state.peer_id.to_string(),
                agent_state: "online".to_string(),
                active_ticket,
                authorized_operator: state.config.authorization.operator_peer_id.clone(),
                listen_addresses: state.listen_addresses.clone(),
            };
            IpcResponse::Status(status)
        }
        IpcRequest::OpenTicket => {
            if state.config.mode() != NodeMode::Managed {
                return IpcResponse::Error(
                    "Tickets can only be opened on managed nodes".to_string(),
                );
            }
            match state.ticket_store.open().await {
                Ok(ticket) => IpcResponse::TicketOpened(ticket),
                Err(e) => IpcResponse::Error(format!("Failed to open ticket: {e}")),
            }
        }
        IpcRequest::ListPeers => {
            if let Some(sender) = &state.p2p_sender {
                let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                if sender
                    .send(fortiq_p2p::P2pCommand::ListPeers { reply: reply_tx })
                    .await
                    .is_ok()
                {
                    match tokio::time::timeout(std::time::Duration::from_secs(3), reply_rx).await {
                        Ok(Ok(peers)) => IpcResponse::Peers(peers),
                        Ok(Err(_)) => {
                            IpcResponse::Error("P2P event loop dropped reply channel".to_string())
                        }
                        Err(_) => IpcResponse::Error(
                            "Timeout waiting for peers from P2P node".to_string(),
                        ),
                    }
                } else {
                    IpcResponse::Error("P2P subsystem channel closed".to_string())
                }
            } else {
                IpcResponse::Peers(Vec::new())
            }
        }
        IpcRequest::ListTickets { state_filter } => {
            match state.ticket_store.db().list_tickets(state_filter) {
                Ok(tickets) => IpcResponse::Tickets(tickets),
                Err(e) => IpcResponse::Error(format!("Échec de listage des tickets: {e}")),
            }
        }
        IpcRequest::GetTicket { ticket_id } => {
            match state.ticket_store.db().get_ticket_detail(&ticket_id) {
                Ok(detail) => IpcResponse::TicketDetail(detail),
                Err(e) => IpcResponse::Error(format!("Échec de consultation du ticket: {e}")),
            }
        }
        IpcRequest::CreateTicket {
            title,
            description,
            priority,
        } => {
            let client_peer = if state.config.mode() == NodeMode::Managed {
                state.peer_id.to_string()
            } else {
                "local".to_string()
            };
            let operator_peer = if state.config.mode() == NodeMode::Managed {
                state
                    .config
                    .authorization
                    .operator_peer_id
                    .clone()
                    .unwrap_or_else(|| "unassigned".to_string())
            } else {
                state.peer_id.to_string()
            };

            match state.ticket_store.db().create_ticket(
                &title,
                &description,
                priority,
                &client_peer,
                &operator_peer,
            ) {
                Ok(record) => {
                    let target_str = if state.config.mode() == NodeMode::Managed {
                        state.config.authorization.operator_peer_id.as_deref()
                    } else {
                        None
                    };
                    if let Some(target) = target_str {
                        if let Ok(peer) = target.parse::<PeerId>() {
                            if let Some(ref sender) = state.p2p_sender {
                                let (reply_tx, _reply_rx) = tokio::sync::oneshot::channel();
                                let _ = sender
                                    .send(fortiq_p2p::P2pCommand::SyncTickets {
                                        peer,
                                        dial: None,
                                        request: fortiq_p2p::TicketSyncRequest::PushTicket(
                                            Box::new(record.clone()),
                                        ),
                                        reply: reply_tx,
                                    })
                                    .await;
                            }
                        }
                    }
                    IpcResponse::TicketCreated(record)
                }
                Err(e) => IpcResponse::Error(format!("Échec de création du ticket: {e}")),
            }
        }
        IpcRequest::UpdateTicketStatus {
            ticket_id,
            state: new_state,
        } => {
            match state.ticket_store.db().update_ticket_state(
                &ticket_id,
                new_state,
                &state.peer_id.to_string(),
            ) {
                Ok(updated) => {
                    if let Some(ref ticket) = updated {
                        let target_str = if ticket.client_peer_id == state.peer_id.to_string() {
                            &ticket.operator_peer_id
                        } else {
                            &ticket.client_peer_id
                        };
                        if let Ok(peer) = target_str.parse::<PeerId>() {
                            if let Some(ref sender) = state.p2p_sender {
                                let (reply_tx, _reply_rx) = tokio::sync::oneshot::channel();
                                let _ = sender
                                    .send(fortiq_p2p::P2pCommand::SyncTickets {
                                        peer,
                                        dial: None,
                                        request: fortiq_p2p::TicketSyncRequest::UpdateStatus {
                                            ticket_id: ticket.id.clone(),
                                            state: new_state,
                                        },
                                        reply: reply_tx,
                                    })
                                    .await;
                            }
                        }
                    }
                    IpcResponse::TicketUpdated(updated)
                }
                Err(e) => IpcResponse::Error(format!("Échec de mise à jour du statut: {e}")),
            }
        }
        IpcRequest::SetRemoteAccess { ticket_id, enabled } => {
            let local_peer_id = state.peer_id.to_string();
            let is_client = state
                .ticket_store
                .db()
                .get_ticket(&ticket_id)
                .ok()
                .flatten()
                .is_some_and(|ticket| ticket.client_peer_id == local_peer_id);
            if !is_client {
                return IpcResponse::Error(
                    "Seul le client propriétaire du ticket peut modifier l'accès à distance"
                        .to_string(),
                );
            }
            match state
                .ticket_store
                .db()
                .set_remote_access(&ticket_id, enabled, &local_peer_id)
            {
                Ok(updated) => {
                    if let Some(ref ticket) = updated {
                        let target_str = if ticket.client_peer_id == state.peer_id.to_string() {
                            &ticket.operator_peer_id
                        } else {
                            &ticket.client_peer_id
                        };
                        if let Ok(peer) = target_str.parse::<PeerId>() {
                            if let Some(ref sender) = state.p2p_sender {
                                let (reply_tx, _reply_rx) = tokio::sync::oneshot::channel();
                                let _ = sender
                                    .send(fortiq_p2p::P2pCommand::SyncTickets {
                                        peer,
                                        dial: None,
                                        request: fortiq_p2p::TicketSyncRequest::SetRemoteAccess {
                                            ticket_id: ticket.id.clone(),
                                            enabled,
                                        },
                                        reply: reply_tx,
                                    })
                                    .await;
                            }
                        }
                    }
                    IpcResponse::TicketUpdated(updated)
                }
                Err(e) => {
                    IpcResponse::Error(format!("Échec de configuration de l'accès à distance: {e}"))
                }
            }
        }
        IpcRequest::SendChatMessage { ticket_id, body } => {
            match state.ticket_store.db().get_ticket(&ticket_id) {
                Ok(Some(ticket)) => {
                    if !ticket.state.permits_work() {
                        return IpcResponse::Error(
                            "Impossible d'envoyer un message : le ticket est fermé".to_string(),
                        );
                    }
                    let msg_id = format!("MSG-{}", uuid::Uuid::new_v4().simple());
                    let now = std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap_or_default()
                        .as_secs();
                    let chat_msg = fortiq_core::ChatMessage {
                        id: msg_id.clone(),
                        ticket_id: ticket_id.clone(),
                        sender_peer_id: state.peer_id.to_string(),
                        body: body.clone(),
                        created_at: now,
                        delivery_state: "PENDING".to_string(),
                    };
                    let _ = state.ticket_store.db().add_chat_message(&chat_msg);
                    let preview: String = body.chars().take(40).collect();
                    let _ = state.ticket_store.db().record_event(
                        &ticket_id,
                        "CHAT_MESSAGE_SENT",
                        &state.peer_id.to_string(),
                        Some(&preview),
                    );

                    let target_str = if ticket.client_peer_id == state.peer_id.to_string() {
                        &ticket.operator_peer_id
                    } else {
                        &ticket.client_peer_id
                    };
                    if let Ok(peer) = target_str.parse::<PeerId>() {
                        if let Some(ref sender) = state.p2p_sender {
                            let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                            let wire_msg = fortiq_p2p::ChatMessageWire {
                                id: msg_id,
                                ticket_id: ticket_id.clone(),
                                body,
                                created_at: now,
                            };
                            let _ = sender
                                .send(fortiq_p2p::P2pCommand::SendChatMessage {
                                    peer,
                                    dial: None,
                                    message: wire_msg,
                                    reply: reply_tx,
                                })
                                .await;
                            match tokio::time::timeout(std::time::Duration::from_secs(8), reply_rx)
                                .await
                            {
                                Ok(Ok(Ok(ack))) => {
                                    if ack.success {
                                        let _ = state
                                            .ticket_store
                                            .db()
                                            .update_message_delivery(&chat_msg.id, "DELIVERED");
                                    } else {
                                        return IpcResponse::Error(ack.error.unwrap_or_else(
                                            || "Erreur du destinataire".to_string(),
                                        ));
                                    }
                                }
                                Ok(Ok(Err(err))) => return IpcResponse::Error(err),
                                _ => {}
                            }
                        }
                    }
                    IpcResponse::MessageSent(chat_msg)
                }
                Ok(None) => IpcResponse::Error("Ticket introuvable".to_string()),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
        IpcRequest::ListMessages { ticket_id } => {
            match state.ticket_store.db().list_messages(&ticket_id) {
                Ok(messages) => IpcResponse::Messages(messages),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
        IpcRequest::SendFile {
            ticket_id,
            file_path,
        } => match state.ticket_store.db().get_ticket(&ticket_id) {
            Ok(Some(ticket)) => {
                if !ticket.state.permits_work() {
                    return IpcResponse::Error(
                        "Impossible d'envoyer un fichier : le ticket est fermé".to_string(),
                    );
                }
                let target_str = if ticket.client_peer_id == state.peer_id.to_string() {
                    &ticket.operator_peer_id
                } else {
                    &ticket.client_peer_id
                };
                let peer = match target_str.parse::<PeerId>() {
                    Ok(p) => p,
                    Err(e) => return IpcResponse::Error(format!("PeerId distant invalide: {e}")),
                };
                if let Some(ref sender) = state.p2p_sender {
                    let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                    if sender
                        .send(fortiq_p2p::P2pCommand::SendFile {
                            peer,
                            dial: None,
                            ticket_id: ticket_id.clone(),
                            file_path: std::path::PathBuf::from(file_path),
                            reply: reply_tx,
                        })
                        .await
                        .is_err()
                    {
                        return IpcResponse::Error("Canal de commande P2P fermé".to_string());
                    }
                    match tokio::time::timeout(std::time::Duration::from_secs(60), reply_rx).await {
                        Ok(Ok(Ok(attachment))) => IpcResponse::FileSent(attachment),
                        Ok(Ok(Err(err))) => IpcResponse::Error(err),
                        Ok(Err(_)) => {
                            IpcResponse::Error("Canal de réponse fichier abandonné".to_string())
                        }
                        Err(_) => IpcResponse::Error(
                            "Délai d'attente dépassé pour l'envoi du fichier (60s)".to_string(),
                        ),
                    }
                } else {
                    IpcResponse::Error("Sous-système P2P indisponible".to_string())
                }
            }
            Ok(None) => IpcResponse::Error("Ticket introuvable".to_string()),
            Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
        },
        IpcRequest::ListAttachments { ticket_id } => {
            match state.ticket_store.db().list_attachments(&ticket_id) {
                Ok(attachments) => IpcResponse::Attachments(attachments),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
        IpcRequest::ListShellSessions { ticket_id } => {
            match state.ticket_store.db().list_shell_sessions(&ticket_id) {
                Ok(sessions) => IpcResponse::ShellSessions(sessions),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
    }
}

async fn handle_terminal_client<S>(stream: S, state: Arc<IpcState>) -> Result<()>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let (ipc_read_half, mut ipc_write) = tokio::io::split(stream);

    if state.config.mode() != NodeMode::Operator {
        let err_msg =
            "{\"status\":\"error\",\"message\":\"Terminal IPC is only permitted in Operator mode\"}\n";
        ipc_write.write_all(err_msg.as_bytes()).await?;
        ipc_write.flush().await?;
        return Ok(());
    }

    let mut reader = BufReader::new(ipc_read_half);
    let mut init_line = String::new();
    let n = reader.read_line(&mut init_line).await?;
    if n == 0 {
        return Ok(());
    }

    let mut ipc_read = reader;

    let init: fortiq_core::ipc::TerminalSessionInit = match serde_json::from_str(init_line.trim()) {
        Ok(val) => val,
        Err(err) => {
            let err_msg = format!(
                "{{\"status\":\"error\",\"message\":\"JSON handshake invalide: {err}\"}}\n"
            );
            ipc_write.write_all(err_msg.as_bytes()).await?;
            ipc_write.flush().await?;
            return Ok(());
        }
    };

    let target_peer: PeerId = match init.peer.parse() {
        Ok(p) => p,
        Err(err) => {
            let err_msg =
                format!("{{\"status\":\"error\",\"message\":\"PeerId invalide: {err}\"}}\n");
            ipc_write.write_all(err_msg.as_bytes()).await?;
            ipc_write.flush().await?;
            return Ok(());
        }
    };

    let dial_addr: Option<libp2p::Multiaddr> = init.dial.and_then(|d| d.parse().ok());

    let p2p_sender = match &state.p2p_sender {
        Some(s) => s.clone(),
        None => {
            let err_msg = "{\"status\":\"error\",\"message\":\"Sous-système P2P indisponible\"}\n";
            ipc_write.write_all(err_msg.as_bytes()).await?;
            ipc_write.flush().await?;
            return Ok(());
        }
    };

    let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
    if p2p_sender
        .send(fortiq_p2p::P2pCommand::OpenShellStream {
            peer: target_peer,
            ticket_id: init.ticket_id,
            dial: dial_addr,
            reply: reply_tx,
        })
        .await
        .is_err()
    {
        let err_msg = "{\"status\":\"error\",\"message\":\"Canal de commande P2P fermé\"}\n";
        ipc_write.write_all(err_msg.as_bytes()).await?;
        ipc_write.flush().await?;
        return Ok(());
    }

    let p2p_stream = match reply_rx.await {
        Ok(Ok(stream)) => stream,
        Ok(Err(err)) => {
            let err_msg = format!("{{\"status\":\"error\",\"message\":\"{err}\"}}\n");
            ipc_write.write_all(err_msg.as_bytes()).await?;
            ipc_write.flush().await?;
            return Ok(());
        }
        Err(_) => {
            let err_msg =
                "{\"status\":\"error\",\"message\":\"Délai dépassé ou canal P2P abandonné\"}\n";
            ipc_write.write_all(err_msg.as_bytes()).await?;
            ipc_write.flush().await?;
            return Ok(());
        }
    };

    ipc_write.write_all(b"{\"status\":\"ok\"}\n").await?;
    ipc_write.flush().await?;

    let (mut p2p_read, mut p2p_write) = tokio::io::split(
        tokio_util::compat::FuturesAsyncReadCompatExt::compat(p2p_stream),
    );

    let initial_resize = fortiq_shell::ShellFrame::Resize {
        cols: init.cols,
        rows: init.rows,
    };
    let _ = initial_resize.write_to(&mut p2p_write).await;

    let mut forward_in = tokio::spawn(async move {
        while let Ok(Some(frame)) = fortiq_shell::ShellFrame::read_from(&mut ipc_read).await {
            if frame.write_to(&mut p2p_write).await.is_err() {
                break;
            }
        }
        let _ = p2p_write.shutdown().await;
    });

    let mut forward_out = tokio::spawn(async move {
        while let Ok(Some(frame)) = fortiq_shell::ShellFrame::read_from(&mut p2p_read).await {
            if frame.write_to(&mut ipc_write).await.is_err() {
                break;
            }
        }
        let _ = ipc_write.shutdown().await;
    });

    tokio::select! {
        _ = &mut forward_in => {
            forward_out.abort();
            let _ = forward_out.await;
        }
        _ = &mut forward_out => {
            forward_in.abort();
            let _ = forward_in.await;
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_terminal_client_rejected_on_managed_mode() {
        let dir = tempfile::tempdir().unwrap();
        let config = Config {
            node: fortiq_core::NodeConfig {
                name: "test-client".to_string(),
            },
            identity: fortiq_core::IdentityConfig {
                path: dir.path().join("id.key"),
            },
            authorization: fortiq_core::AuthorizationConfig {
                operator_peer_id: Some(
                    "12D3KooWDpJ7As7BWAwRMfu1VU2WCqNjvq387JEYKDBj4kx6nXTN".to_string(),
                ),
            },
            network: fortiq_core::NetworkConfig::default(),
            capabilities: fortiq_core::CapabilitiesConfig::default(),
            ticket: fortiq_core::TicketConfig::default(),
            ipc: fortiq_core::IpcConfig::default(),
        };
        assert_eq!(config.mode(), NodeMode::Managed);
        let ticket_path = dir.path().join("ticket.json");
        let state = Arc::new(IpcState {
            config,
            peer_id: PeerId::random(),
            listen_addresses: vec![],
            ticket_store: TicketStore::new(ticket_path),
            p2p_sender: None,
        });

        let (client_io, server_io) = tokio::io::duplex(1024);
        let handle = tokio::spawn(async move { handle_terminal_client(server_io, state).await });

        let mut reader = BufReader::new(client_io);
        let mut line = String::new();
        reader.read_line(&mut line).await.unwrap();
        assert!(line.contains("Terminal IPC is only permitted in Operator mode"));

        let res = handle.await.unwrap();
        assert!(res.is_ok());
    }
}
