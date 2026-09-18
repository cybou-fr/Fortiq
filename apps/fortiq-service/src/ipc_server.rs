use std::sync::Arc;

use anyhow::Result;
use fortiq_core::{
    canonical::{
        control::{derive_owner_id, Genesis},
        portable::{
            certificate::{OperatorCapabilities, OperatorSessionCertificate},
            mnemonic::{parse_mnemonic_phrase, MnemonicDeriver},
            workspace::MemoryWorkspace,
        },
        signing::{Ed25519Signer, Signer},
        types::{EntityId, OwnerId},
    },
    ipc::{DaemonStatus, IpcRequest, IpcResponse, OperatorSessionStatus},
    Config, TicketDb,
};
use libp2p::PeerId;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
use tokio_util::sync::CancellationToken;

pub const MAX_IPC_LINE_BYTES: usize = 64 * 1024;

pub struct ActiveOperatorSession {
    pub session_id: uuid::Uuid,
    pub owner_id: OwnerId,
    pub cert: OperatorSessionCertificate,
    pub workspace: MemoryWorkspace,
    pub expires_at: u64,
    pub session_signer: Arc<Ed25519Signer>,
    pub cancellation_token: CancellationToken,
}

pub struct IpcState {
    pub config: Config,
    pub peer_id: PeerId,
    pub listen_addresses: Vec<String>,
    pub ticket_store: TicketDb,
    pub p2p_sender: Option<tokio::sync::mpsc::Sender<fortiq_p2p::P2pCommand>>,
    pub operator_session: Arc<tokio::sync::RwLock<Option<ActiveOperatorSession>>>,
    pub genesis: Option<Genesis>,
}

pub async fn is_operator_authorized_for(state: &IpcState, required: u32) -> bool {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let mut session_guard = state.operator_session.write().await;
    if let Some(session) = session_guard.as_ref() {
        if session.expires_at > now && session.cert.capabilities.has(required) {
            return true;
        }
        if now >= session.expires_at {
            if let Some(mut expired) = session_guard.take() {
                expired.cancellation_token.cancel();
                expired.workspace.wipe();
            }
        }
    }
    false
}

pub async fn is_operator_authorized(state: &IpcState) -> bool {
    is_operator_authorized_for(state, OperatorCapabilities::SHELL_EXEC).await
}

fn staged_components<'a>(
    canonical_spool: &std::path::Path,
    canonical_source: &'a std::path::Path,
) -> std::result::Result<(&'a std::ffi::OsStr, &'a std::ffi::OsStr), String> {
    let parent = canonical_source
        .parent()
        .ok_or_else(|| "Chemin staged invalide".to_string())?;
    if parent.parent() != Some(canonical_spool) {
        return Err("Le service refuse tout fichier situé hors du spool utilisateur".to_string());
    }
    let user = parent
        .file_name()
        .ok_or_else(|| "Répertoire utilisateur staged invalide".to_string())?;
    let name = canonical_source
        .file_name()
        .ok_or_else(|| "Nom de fichier staged invalide".to_string())?;
    Ok((user, name))
}

#[cfg(unix)]
fn open_staged_file(
    canonical_spool: &std::path::Path,
    canonical_source: &std::path::Path,
) -> std::result::Result<std::fs::File, String> {
    use std::os::fd::{AsRawFd, FromRawFd};
    use std::os::unix::ffi::OsStrExt;

    let (user, name) = staged_components(canonical_spool, canonical_source)?;
    let user = std::ffi::CString::new(user.as_bytes())
        .map_err(|_| "Répertoire utilisateur staged invalide".to_string())?;
    let name = std::ffi::CString::new(name.as_bytes())
        .map_err(|_| "Nom de fichier staged invalide".to_string())?;
    let root = std::fs::File::open(canonical_spool)
        .map_err(|error| format!("Spool inaccessible: {error}"))?;
    let user_fd = unsafe {
        libc::openat(
            root.as_raw_fd(),
            user.as_ptr(),
            libc::O_RDONLY | libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC,
        )
    };
    if user_fd < 0 {
        return Err(format!(
            "Répertoire staged non sécurisé: {}",
            std::io::Error::last_os_error()
        ));
    }
    let user_dir = unsafe { std::fs::File::from_raw_fd(user_fd) };
    let file_fd = unsafe {
        libc::openat(
            user_dir.as_raw_fd(),
            name.as_ptr(),
            libc::O_RDONLY | libc::O_NOFOLLOW | libc::O_CLOEXEC,
        )
    };
    if file_fd < 0 {
        return Err(format!(
            "Fichier staged non sécurisé: {}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(unsafe { std::fs::File::from_raw_fd(file_fd) })
}

#[cfg(windows)]
fn open_staged_file(
    canonical_spool: &std::path::Path,
    canonical_source: &std::path::Path,
) -> std::result::Result<std::fs::File, String> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        GetFinalPathNameByHandleW, FILE_FLAG_OPEN_REPARSE_POINT, FILE_NAME_NORMALIZED,
        VOLUME_NAME_DOS,
    };

    let file = std::fs::OpenOptions::new()
        .read(true)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
        .open(canonical_source)
        .map_err(|error| format!("Fichier staged inaccessible: {error}"))?;
    let mut buffer = vec![0u16; 32_768];
    let length = unsafe {
        GetFinalPathNameByHandleW(
            file.as_raw_handle() as _,
            buffer.as_mut_ptr(),
            buffer.len() as u32,
            FILE_NAME_NORMALIZED | VOLUME_NAME_DOS,
        )
    };
    if length == 0 || length as usize >= buffer.len() {
        return Err("Impossible de vérifier le handle du fichier staged".to_string());
    }
    buffer.truncate(length as usize);
    let final_path = std::path::PathBuf::from(String::from_utf16_lossy(&buffer));
    staged_components(canonical_spool, &final_path)?;
    Ok(file)
}

async fn import_staged_upload(
    ticket_store: &TicketDb,
    staged_path: &str,
) -> std::result::Result<std::path::PathBuf, String> {
    let spool = fortiq_core::ipc::upload_spool_dir();
    import_staged_upload_from(ticket_store, staged_path, &spool).await
}

async fn import_staged_upload_from(
    ticket_store: &TicketDb,
    staged_path: &str,
    spool: &std::path::Path,
) -> std::result::Result<std::path::PathBuf, String> {
    tokio::fs::create_dir_all(&spool)
        .await
        .map_err(|error| format!("Impossible de préparer le spool: {error}"))?;
    let canonical_spool = tokio::fs::canonicalize(&spool)
        .await
        .map_err(|error| format!("Spool inaccessible: {error}"))?;
    let canonical_source = tokio::fs::canonicalize(staged_path)
        .await
        .map_err(|error| format!("Fichier staged inaccessible: {error}"))?;
    staged_components(&canonical_spool, &canonical_source)?;
    let staged_name = canonical_source
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| "Nom de fichier staged invalide".to_string())?;
    let (id, original_name) = staged_name
        .split_once('_')
        .ok_or_else(|| "Nom de fichier staged invalide".to_string())?;
    uuid::Uuid::parse_str(id).map_err(|_| "Identifiant de staging invalide".to_string())?;
    let source = open_staged_file(&canonical_spool, &canonical_source)?;
    let metadata = source
        .metadata()
        .map_err(|error| format!("Metadata staged inaccessible: {error}"))?;
    if !metadata.is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() > fortiq_p2p::MAX_FILE_SIZE
    {
        return Err("Le fichier staged est invalide ou trop volumineux".to_string());
    }

    let private_dir = ticket_store.storage_dir().join("outbox-files");
    tokio::fs::create_dir_all(&private_dir)
        .await
        .map_err(|error| format!("Impossible de créer le spool privé: {error}"))?;
    let destination = private_dir.join(format!(
        "{}_{}",
        uuid::Uuid::new_v4().simple(),
        original_name
    ));
    let partial_destination = destination.with_extension("part");
    let source = tokio::fs::File::from_std(source);
    let mut limited_source = tokio::io::AsyncReadExt::take(source, fortiq_p2p::MAX_FILE_SIZE + 1);
    let mut destination_file = tokio::fs::File::create(&partial_destination)
        .await
        .map_err(|error| format!("Impossible de créer le fichier privé: {error}"))?;
    let copied = tokio::io::copy(&mut limited_source, &mut destination_file)
        .await
        .map_err(|error| format!("Impossible d'importer le fichier staged: {error}"))?;
    if copied > fortiq_p2p::MAX_FILE_SIZE {
        drop(destination_file);
        let _ = tokio::fs::remove_file(&partial_destination).await;
        return Err("Le fichier staged a dépassé la taille maximale pendant la copie".to_string());
    }
    destination_file
        .sync_all()
        .await
        .map_err(|error| format!("Impossible de synchroniser le fichier privé: {error}"))?;
    drop(destination_file);
    tokio::fs::rename(&partial_destination, &destination)
        .await
        .map_err(|error| format!("Impossible de finaliser le fichier privé: {error}"))?;
    let _ = tokio::fs::remove_file(&canonical_source).await;
    Ok(destination)
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
    tracing::info!("Starting Windows Named Pipe IPC server at {}", pipe_name);

    let mut server = create_windows_pipe(&pipe_name, true, false)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Named pipe connection failed: {err}");
            server = create_windows_pipe(&pipe_name, false, false)?;
            continue;
        }

        let client = server;
        server = create_windows_pipe(&pipe_name, false, false)?;

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
    tracing::info!("Starting Windows Terminal Named Pipe at {}", pipe_name);

    let mut server = create_windows_pipe(&pipe_name, true, true)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Terminal named pipe connection failed: {err}");
            server = create_windows_pipe(&pipe_name, false, true)?;
            continue;
        }

        let client = server;
        server = create_windows_pipe(&pipe_name, false, true)?;

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
    _is_terminal: bool,
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

    let sddl_str = {
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
            setup_unix_socket_permissions_and_group(p_str, 0o770);
        }
    }
    let listener = UnixListener::bind(&path)?;
    #[cfg(unix)]
    {
        setup_unix_socket_permissions_and_group(&path, 0o660);
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
            let is_unlocked = is_operator_authorized(state).await;
            let status = DaemonStatus {
                product: "FORTIQ".to_string(),
                version: env!("CARGO_PKG_VERSION").to_string(),
                peer_id: state.peer_id.to_string(),
                agent_state: "online".to_string(),
                active_ticket,
                listen_addresses: state.listen_addresses.clone(),
                is_operator_unlocked: is_unlocked,
            };
            IpcResponse::Status(status)
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
            match state.ticket_store.list_tickets(state_filter) {
                Ok(tickets) => IpcResponse::Tickets(tickets),
                Err(e) => IpcResponse::Error(format!("Échec de listage des tickets: {e}")),
            }
        }
        IpcRequest::GetTicket { ticket_id } => {
            match state.ticket_store.get_ticket_detail(&ticket_id) {
                Ok(detail) => IpcResponse::TicketDetail(detail),
                Err(e) => IpcResponse::Error(format!("Échec de consultation du ticket: {e}")),
            }
        }
        IpcRequest::CreateTicket {
            title,
            description,
            priority,
        } => {
            let client_peer = state.peer_id.to_string();

            match state
                .ticket_store
                .create_ticket(&title, &description, priority, &client_peer)
            {
                Ok(record) => {
                    let target_str = state.config.network.bootstrap_peer.as_deref();
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
            if !is_operator_authorized_for(state, OperatorCapabilities::TICKET_MANAGE).await {
                return IpcResponse::Error(
                    "Seule une session Operator/Admin active peut modifier le cycle de vie du ticket"
                        .to_string(),
                );
            }
            let local_peer_id = state.peer_id.to_string();
            let current = match state.ticket_store.get_ticket(&ticket_id) {
                Ok(Some(ticket)) => ticket,
                Ok(None) => return IpcResponse::Error("Ticket introuvable".to_string()),
                Err(error) => return IpcResponse::Error(format!("Erreur: {error}")),
            };
            if current.client_peer_id != local_peer_id {
                let peer = match current.client_peer_id.parse::<PeerId>() {
                    Ok(peer) => peer,
                    Err(error) => {
                        return IpcResponse::Error(format!("PeerId client invalide: {error}"));
                    }
                };
                let Some(sender) = state.p2p_sender.as_ref() else {
                    return IpcResponse::Error("Sous-système P2P indisponible".to_string());
                };
                let (certificate, session_signer) = {
                    let session = state.operator_session.read().await;
                    let Some(session) = session.as_ref() else {
                        return IpcResponse::Error("Session opérateur inactive".to_string());
                    };
                    (session.cert.clone(), session.session_signer.clone())
                };
                let network_id = match state.genesis.as_ref() {
                    Some(genesis) => genesis.tbs.network_id,
                    None => return IpcResponse::Error("Genesis indisponible".to_string()),
                };
                let mut mutation = fortiq_p2p::TicketStateMutation {
                    network_id,
                    ticket_id: ticket_id.clone(),
                    expected_revision: current.revision,
                    new_state,
                    request_id: *uuid::Uuid::new_v4().as_bytes(),
                    operator_transport_peer_id: local_peer_id.clone(),
                    certificate,
                    signature: Vec::new(),
                };
                mutation.signature = match session_signer.sign(&mutation.signing_payload()) {
                    Ok(signature) => signature,
                    Err(error) => {
                        return IpcResponse::Error(format!(
                            "Signature lifecycle impossible: {error}"
                        ));
                    }
                };
                let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                if sender
                    .send(fortiq_p2p::P2pCommand::SyncTickets {
                        peer,
                        dial: None,
                        request: fortiq_p2p::TicketSyncRequest::UpdateStatusSigned(Box::new(
                            mutation,
                        )),
                        reply: reply_tx,
                    })
                    .await
                    .is_err()
                {
                    return IpcResponse::Error("Canal de commande P2P fermé".to_string());
                }
                return match tokio::time::timeout(std::time::Duration::from_secs(10), reply_rx)
                    .await
                {
                    Ok(Ok(Ok(fortiq_p2p::TicketSyncResponse::MutationApplied(ticket)))) => {
                        IpcResponse::TicketUpdated(Some(*ticket))
                    }
                    Ok(Ok(Ok(fortiq_p2p::TicketSyncResponse::MutationRejected {
                        message,
                        ..
                    }))) => IpcResponse::Error(message),
                    Ok(Ok(Err(error))) => IpcResponse::Error(error),
                    _ => IpcResponse::Error(
                        "Mutation mise en attente jusqu'à la reconnexion du client".to_string(),
                    ),
                };
            }

            match state
                .ticket_store
                .update_ticket_state(&ticket_id, new_state, &local_peer_id)
            {
                Ok(updated) => {
                    if let Some(ref ticket) = updated {
                        if let Some(target) = state.config.network.bootstrap_peer.as_deref() {
                            if let Ok(peer) = target.parse::<PeerId>() {
                                if let Some(ref sender) = state.p2p_sender {
                                    let (reply_tx, _reply_rx) = tokio::sync::oneshot::channel();
                                    let _ = sender
                                        .send(fortiq_p2p::P2pCommand::SyncTickets {
                                            peer,
                                            dial: None,
                                            request: fortiq_p2p::TicketSyncRequest::PushTicket(
                                                Box::new(ticket.clone()),
                                            ),
                                            reply: reply_tx,
                                        })
                                        .await;
                                }
                            }
                        }
                    }
                    IpcResponse::TicketUpdated(updated)
                }
                Err(e) => IpcResponse::Error(format!("Échec de mise à jour du statut: {e}")),
            }
        }
        IpcRequest::SendChatMessage { ticket_id, body } => {
            match state.ticket_store.get_ticket(&ticket_id) {
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
                    if let Err(error) = state.ticket_store.add_chat_message(&chat_msg) {
                        return IpcResponse::Error(format!(
                            "Échec de persistance du message local: {error}"
                        ));
                    }
                    let preview: String = body.chars().take(40).collect();
                    let _ = state.ticket_store.record_event(
                        &ticket_id,
                        "CHAT_MESSAGE_SENT",
                        &state.peer_id.to_string(),
                        Some(&preview),
                    );

                    let target_str = if ticket.client_peer_id == state.peer_id.to_string() {
                        state.config.network.bootstrap_peer.as_deref().unwrap_or("")
                    } else {
                        &ticket.client_peer_id
                    };
                    if let Ok(peer) = target_str.parse::<PeerId>() {
                        if let Some(ref sender) = state.p2p_sender {
                            let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                            let authority = if ticket.client_peer_id != state.peer_id.to_string() {
                                let session = state.operator_session.read().await;
                                let Some(session) = session.as_ref() else {
                                    return IpcResponse::Error(
                                        "Session opérateur inactive".to_string(),
                                    );
                                };
                                if !session.cert.capabilities.has(OperatorCapabilities::WRITE) {
                                    return IpcResponse::Error(
                                        "Capability WRITE requise".to_string(),
                                    );
                                }
                                let transport_peer_id = state.peer_id.to_string();
                                let signature = match session.session_signer.sign(
                                    &fortiq_p2p::OperatorSessionProof::signing_payload(
                                        "chat",
                                        &ticket_id,
                                        body.as_bytes(),
                                        &transport_peer_id,
                                        OperatorCapabilities::WRITE,
                                    ),
                                ) {
                                    Ok(signature) => signature,
                                    Err(error) => {
                                        return IpcResponse::Error(format!(
                                            "Signature chat impossible: {error}"
                                        ))
                                    }
                                };
                                match fortiq_p2p::OperatorSessionProof::from_certificate(
                                    &session.cert,
                                    transport_peer_id,
                                    OperatorCapabilities::WRITE,
                                    signature,
                                ) {
                                    Ok(proof) => Some(proof),
                                    Err(error) => {
                                        return IpcResponse::Error(error);
                                    }
                                }
                            } else {
                                None
                            };
                            let wire_msg = fortiq_p2p::ChatMessageWire {
                                id: msg_id,
                                ticket_id: ticket_id.clone(),
                                body,
                                created_at: now,
                                authority,
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
            match state.ticket_store.list_messages(&ticket_id) {
                Ok(messages) => IpcResponse::Messages(messages),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
        IpcRequest::SendFile {
            ticket_id,
            staged_path,
        } => match state.ticket_store.get_ticket(&ticket_id) {
            Ok(Some(ticket)) => {
                if !ticket.state.permits_work() {
                    return IpcResponse::Error(
                        "Impossible d'envoyer un fichier : le ticket est fermé".to_string(),
                    );
                }
                let target_str = if ticket.client_peer_id == state.peer_id.to_string() {
                    state.config.network.bootstrap_peer.as_deref().unwrap_or("")
                } else {
                    &ticket.client_peer_id
                };
                let peer = match target_str.parse::<PeerId>() {
                    Ok(p) => p,
                    Err(e) => return IpcResponse::Error(format!("PeerId distant invalide: {e}")),
                };
                if let Some(ref sender) = state.p2p_sender {
                    let (certificate, session_signer) = if ticket.client_peer_id
                        != state.peer_id.to_string()
                    {
                        let session = state.operator_session.read().await;
                        let Some(session) = session.as_ref() else {
                            return IpcResponse::Error("Session opérateur inactive".to_string());
                        };
                        if !session
                            .cert
                            .capabilities
                            .has(OperatorCapabilities::FILE_TRANSFER)
                        {
                            return IpcResponse::Error(
                                "Capability FILE_TRANSFER requise".to_string(),
                            );
                        }
                        (
                            Some(session.cert.clone()),
                            Some(session.session_signer.clone()),
                        )
                    } else {
                        (None, None)
                    };
                    let file_path =
                        match import_staged_upload(&state.ticket_store, &staged_path).await {
                            Ok(path) => path,
                            Err(error) => return IpcResponse::Error(error),
                        };
                    let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                    if sender
                        .send(fortiq_p2p::P2pCommand::SendFile {
                            peer,
                            dial: None,
                            ticket_id: ticket_id.clone(),
                            file_path,
                            certificate,
                            session_signer,
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
            match state.ticket_store.list_attachments(&ticket_id) {
                Ok(attachments) => IpcResponse::Attachments(attachments),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
        IpcRequest::ListShellSessions { ticket_id } => {
            match state.ticket_store.list_shell_sessions(&ticket_id) {
                Ok(sessions) => IpcResponse::ShellSessions(sessions),
                Err(e) => IpcResponse::Error(format!("Erreur: {e}")),
            }
        }
        IpcRequest::UnlockOperator { mnemonic } => {
            let genesis = match state.genesis.as_ref() {
                Some(genesis) => genesis,
                None => {
                    return IpcResponse::Error(
                        "Genesis canonique indisponible; déverrouillage refusé".to_string(),
                    );
                }
            };
            if let Err(error) = genesis.verify() {
                return IpcResponse::Error(format!("Genesis canonique invalide: {error}"));
            }
            let entropy = match parse_mnemonic_phrase(&mnemonic) {
                Ok(e) => e,
                Err(err) => {
                    return IpcResponse::Error(format!("Phrase mnémonique invalide: {err}"));
                }
            };
            let deriver = MnemonicDeriver::new(&entropy);
            let root_seed = match deriver.derive_root_signing_seed() {
                Ok(s) => s,
                Err(err) => {
                    return IpcResponse::Error(format!("Échec dérivation clé racine: {err}"));
                }
            };
            let segment_master_seed = match deriver.derive_segment_master_seed() {
                Ok(seed) => seed,
                Err(err) => {
                    return IpcResponse::Error(format!(
                        "Échec dérivation clé maître de segment: {err}"
                    ));
                }
            };

            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_secs();
            let ttl = 3600u64; // 1 hour session
            let expires_at = now + ttl;

            let root_signer = Ed25519Signer::from_seed(*root_seed.as_bytes());
            let root_public_key = root_signer.public_key();
            let owner_id = derive_owner_id(&root_public_key);
            if owner_id != genesis.tbs.owner_id
                || root_public_key.as_slice()
                    != genesis.tbs.owner_root_signing_public_key.as_slice()
            {
                return IpcResponse::Error(
                    "Cette phrase mnémonique ne correspond pas au Owner de Genesis".to_string(),
                );
            }
            let net_id = genesis.tbs.network_id;
            let host_entity =
                EntityId::from_bytes(*blake3::hash(&state.peer_id.to_bytes()).as_bytes());
            let op_entity = host_entity;
            let nonce = *uuid::Uuid::new_v4().as_bytes();

            let session_seed = match deriver.derive_operator_session_seed(&nonce) {
                Ok(s) => s,
                Err(err) => {
                    return IpcResponse::Error(format!("Échec dérivation session: {err}"));
                }
            };
            let session_signer = Arc::new(Ed25519Signer::from_seed(*session_seed.as_bytes()));
            let session_pubkey = session_signer.public_key();
            let op_key = session_signer.key_id();
            let capabilities = OperatorCapabilities::from_names([
                "admin",
                "read",
                "write",
                "shell",
                "file",
                "ticket",
                "diagnostics",
            ]);

            let cert = match OperatorSessionCertificate::issue(
                net_id,
                owner_id,
                host_entity,
                op_entity,
                op_key,
                session_pubkey,
                capabilities,
                now.saturating_sub(10),
                expires_at,
                nonce,
                &root_signer,
            ) {
                Ok(c) => c,
                Err(err) => {
                    return IpcResponse::Error(format!("Échec émission certificat: {err}"));
                }
            };

            let mut workspace = MemoryWorkspace::new();
            workspace.unlock(net_id, owner_id, segment_master_seed);
            let session_id = uuid::Uuid::new_v4();
            let cancellation_token = CancellationToken::new();

            {
                let mut guard = state.operator_session.write().await;
                if let Some(mut previous) = guard.take() {
                    previous.cancellation_token.cancel();
                    previous.workspace.wipe();
                }
                *guard = Some(ActiveOperatorSession {
                    session_id,
                    owner_id,
                    cert,
                    workspace,
                    expires_at,
                    session_signer,
                    cancellation_token,
                });
            }

            let operator_session = Arc::clone(&state.operator_session);
            tokio::spawn(async move {
                tokio::time::sleep(std::time::Duration::from_secs(ttl)).await;
                let mut guard = operator_session.write().await;
                let is_current = guard
                    .as_ref()
                    .is_some_and(|session| session.session_id == session_id);
                if is_current {
                    if let Some(mut expired) = guard.take() {
                        expired.cancellation_token.cancel();
                        expired.workspace.wipe();
                    }
                }
            });

            IpcResponse::OperatorStatus(OperatorSessionStatus {
                is_unlocked: true,
                owner_id: Some(owner_id.to_string()),
                expires_at: Some(expires_at),
                capabilities: capabilities.to_names(),
            })
        }
        IpcRequest::LockOperator => {
            let mut guard = state.operator_session.write().await;
            if let Some(mut session) = guard.take() {
                session.cancellation_token.cancel();
                session.workspace.wipe();
                session.session_signer = Arc::new(Ed25519Signer::from_seed([0u8; 32]));
            }
            IpcResponse::Success
        }
        IpcRequest::GetOperatorStatus => {
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_secs();
            let mut guard = state.operator_session.write().await;
            if let Some(session) = guard.as_ref() {
                if now >= session.expires_at {
                    if let Some(mut expired) = guard.take() {
                        expired.cancellation_token.cancel();
                        expired.workspace.wipe();
                    }
                    return IpcResponse::OperatorStatus(OperatorSessionStatus {
                        is_unlocked: false,
                        owner_id: None,
                        expires_at: None,
                        capabilities: Vec::new(),
                    });
                }
                return IpcResponse::OperatorStatus(OperatorSessionStatus {
                    is_unlocked: true,
                    owner_id: Some(session.owner_id.to_string()),
                    expires_at: Some(session.expires_at),
                    capabilities: session.cert.capabilities.to_names(),
                });
            }
            IpcResponse::OperatorStatus(OperatorSessionStatus {
                is_unlocked: false,
                owner_id: None,
                expires_at: None,
                capabilities: Vec::new(),
            })
        }
    }
}

async fn handle_terminal_client<S>(stream: S, state: Arc<IpcState>) -> Result<()>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let (ipc_read_half, mut ipc_write) = tokio::io::split(stream);

    if !is_operator_authorized(state.as_ref()).await {
        let err_msg =
            "{\"status\":\"error\",\"message\":\"Terminal IPC is only permitted in Operator mode or with an active Operator session\"}\n";
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

    if target_peer == state.peer_id {
        let err_msg =
            "{\"status\":\"error\",\"message\":\"Cannot open terminal session to local node\"}\n";
        ipc_write.write_all(err_msg.as_bytes()).await?;
        ipc_write.flush().await?;
        return Ok(());
    }

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

    let ticket_id = match init.ticket_id {
        Some(ticket_id) if !ticket_id.trim().is_empty() => ticket_id,
        _ => {
            ipc_write
                .write_all(
                    b"{\"status\":\"error\",\"message\":\"Ticket requis pour shell/next\"}\n",
                )
                .await?;
            return Ok(());
        }
    };
    let _ticket = match state.ticket_store.get_ticket(&ticket_id)? {
        Some(ticket) => ticket,
        None => {
            ipc_write
                .write_all(b"{\"status\":\"error\",\"message\":\"Ticket introuvable\"}\n")
                .await?;
            return Ok(());
        }
    };
    let (certificate, session_signer, cancellation_token) = {
        let session = state.operator_session.read().await;
        let Some(session) = session.as_ref() else {
            ipc_write
                .write_all(b"{\"status\":\"error\",\"message\":\"Operator session inactive\"}\n")
                .await?;
            ipc_write.flush().await?;
            return Ok(());
        };
        (
            session.cert.clone(),
            session.session_signer.clone(),
            session.cancellation_token.clone(),
        )
    };
    let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
    if p2p_sender
        .send(fortiq_p2p::P2pCommand::OpenShellNext(Box::new(
            fortiq_p2p::OpenShellNextCommand {
                peer: target_peer,
                ticket_id,
                certificate,
                session_signer,
                dial: dial_addr,
                reply: reply_tx,
            },
        )))
        .await
        .is_err()
    {
        let err_msg = "{\"status\":\"error\",\"message\":\"Canal de commande P2P fermé\"}\n";
        ipc_write.write_all(err_msg.as_bytes()).await?;
        ipc_write.flush().await?;
        return Ok(());
    }

    let p2p_stream = match tokio::select! {
        _ = cancellation_token.cancelled() => Err("La session opérateur a été verrouillée ou a expiré".to_string()),
        result = reply_rx => result.map_err(|_| "Délai dépassé ou canal P2P abandonné".to_string()),
    } {
        Ok(Ok(stream)) => stream,
        Ok(Err(err)) => {
            let err_msg = format!("{{\"status\":\"error\",\"message\":\"{err}\"}}\n");
            ipc_write.write_all(err_msg.as_bytes()).await?;
            ipc_write.flush().await?;
            return Ok(());
        }
        Err(err) => {
            let err_msg = format!("{{\"status\":\"error\",\"message\":\"{err}\"}}\n");
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
        _ = cancellation_token.cancelled() => {
            forward_in.abort();
            forward_out.abort();
            let _ = forward_in.await;
            let _ = forward_out.await;
        }
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
    use fortiq_core::canonical::{
        codec::to_canonical_cbor,
        control::{GenesisTbs, GENESIS_SIG_DOMAIN},
        crypto::keys::MnemonicEntropy,
        portable::mnemonic::entropy_to_mnemonic,
        types::{CryptoProfileId, NetworkId},
    };

    fn valid_test_mnemonic() -> &'static str {
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art"
    }

    fn test_genesis_for_mnemonic(mnemonic: &str) -> Genesis {
        let entropy = parse_mnemonic_phrase(mnemonic).unwrap();
        let root_seed = MnemonicDeriver::new(&entropy)
            .derive_root_signing_seed()
            .unwrap();
        let signer = Ed25519Signer::from_seed(*root_seed.as_bytes());
        let public_key = signer.public_key();
        let tbs = GenesisTbs {
            version: 1,
            network_id: NetworkId::from_bytes([0x77; 32]),
            owner_id: derive_owner_id(&public_key),
            owner_root_signing_public_key: public_key.to_vec(),
            recovery_public_key: None,
            initial_crypto_profile: CryptoProfileId::FortiqClassicalDev1,
            initial_policy_hash: [0x88; 32],
            created_at: 1,
        };
        let mut payload = GENESIS_SIG_DOMAIN.to_vec();
        payload.extend_from_slice(&to_canonical_cbor(&tbs).unwrap());
        let signature = signer.sign(&payload).unwrap();
        Genesis { tbs, signature }
    }

    #[tokio::test]
    async fn staged_upload_rejects_files_outside_the_public_spool() {
        let dir = tempfile::tempdir().unwrap();
        let spool = dir.path().join("public-spool");
        tokio::fs::create_dir_all(&spool).await.unwrap();
        let outside = dir
            .path()
            .join(format!("{}_secret.txt", uuid::Uuid::new_v4().simple()));
        tokio::fs::write(&outside, b"secret").await.unwrap();
        let store = TicketDb::new(dir.path().join("tickets.json"));

        let error = import_staged_upload_from(&store, outside.to_str().unwrap(), &spool)
            .await
            .unwrap_err();

        assert!(error.contains("hors du spool"));
        assert!(outside.exists());
    }

    #[tokio::test]
    async fn staged_upload_is_copied_from_the_validated_open_handle() {
        let dir = tempfile::tempdir().unwrap();
        let spool = dir.path().join("public-spool");
        let user_spool = spool.join("user-key");
        tokio::fs::create_dir_all(&user_spool).await.unwrap();
        let staged = user_spool.join(format!("{}_evidence.txt", uuid::Uuid::new_v4().simple()));
        tokio::fs::write(&staged, b"stable bytes").await.unwrap();
        let store = TicketDb::new(dir.path().join("tickets.json"));

        let imported = import_staged_upload_from(&store, staged.to_str().unwrap(), &spool)
            .await
            .unwrap();

        assert_eq!(tokio::fs::read(imported).await.unwrap(), b"stable bytes");
        assert!(!staged.exists());
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn staged_upload_rejects_a_symlink_to_an_outside_file() {
        use std::os::unix::fs::symlink;

        let dir = tempfile::tempdir().unwrap();
        let spool = dir.path().join("public-spool");
        let user_spool = spool.join("user-key");
        tokio::fs::create_dir_all(&user_spool).await.unwrap();
        let outside = dir.path().join("root-secret.txt");
        tokio::fs::write(&outside, b"secret").await.unwrap();
        let staged = user_spool.join(format!("{}_link", uuid::Uuid::new_v4().simple()));
        symlink(&outside, &staged).unwrap();
        let store = TicketDb::new(dir.path().join("tickets.json"));

        assert!(
            import_staged_upload_from(&store, staged.to_str().unwrap(), &spool)
                .await
                .is_err()
        );
    }

    #[tokio::test]
    async fn test_terminal_client_rejected_without_operator_session() {
        let dir = tempfile::tempdir().unwrap();
        let config = Config {
            node: fortiq_core::NodeConfig {
                name: "test-client".to_string(),
            },
            identity: fortiq_core::IdentityConfig {
                path: dir.path().join("id.key"),
            },
            network: fortiq_core::NetworkConfig::default(),
            capabilities: fortiq_core::CapabilitiesConfig::default(),
            ticket: fortiq_core::TicketConfig::default(),
            ipc: fortiq_core::IpcConfig::default(),
        };
        let ticket_path = dir.path().join("ticket.json");
        let state = Arc::new(IpcState {
            config,
            peer_id: PeerId::random(),
            listen_addresses: vec![],
            ticket_store: TicketDb::new(ticket_path),
            p2p_sender: None,
            operator_session: Arc::new(tokio::sync::RwLock::new(None)),
            genesis: None,
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

    #[tokio::test]
    async fn test_operator_unlock_lock_and_terminal_authorization() {
        let dir = tempfile::tempdir().unwrap();
        let config = Config {
            node: fortiq_core::NodeConfig {
                name: "test-client".to_string(),
            },
            identity: fortiq_core::IdentityConfig {
                path: dir.path().join("id.key"),
            },
            network: fortiq_core::NetworkConfig::default(),
            capabilities: fortiq_core::CapabilitiesConfig::default(),
            ticket: fortiq_core::TicketConfig::default(),
            ipc: fortiq_core::IpcConfig::default(),
        };
        let ticket_path = dir.path().join("ticket.json");
        let state = Arc::new(IpcState {
            config,
            peer_id: PeerId::random(),
            listen_addresses: vec![],
            ticket_store: TicketDb::new(ticket_path),
            p2p_sender: None,
            operator_session: Arc::new(tokio::sync::RwLock::new(None)),
            genesis: Some(test_genesis_for_mnemonic(valid_test_mnemonic())),
        });

        // 1. Initial status: is_operator_authorized must be false
        assert!(!is_operator_authorized(&state).await);

        // 2. Unlock with invalid mnemonic fails
        let res = process_request(
            IpcRequest::UnlockOperator {
                mnemonic: "invalid phrase not 24 words".to_string(),
            },
            &state,
        )
        .await;
        match res {
            IpcResponse::Error(msg) => {
                assert!(msg.contains("Phrase mnémonique invalide"));
            }
            other => panic!("Expected Error response, got {other:?}"),
        }

        // 3. Unlock with valid 24-word BIP-39 mnemonic
        let valid_mnemonic = valid_test_mnemonic();
        let res = process_request(
            IpcRequest::UnlockOperator {
                mnemonic: valid_mnemonic.to_string(),
            },
            &state,
        )
        .await;
        match res {
            IpcResponse::OperatorStatus(status) => {
                assert!(status.is_unlocked);
                assert!(status.owner_id.is_some());
                assert!(status.expires_at.is_some());
                assert!(status.capabilities.contains(&"shell".to_string()));
                assert!(status.capabilities.contains(&"write".to_string()));
                assert!(status.capabilities.contains(&"ticket".to_string()));
            }
            other => panic!("Expected OperatorStatus response, got {other:?}"),
        }

        // A valid mnemonic for a different Owner must fail closed.
        let wrong_mnemonic = entropy_to_mnemonic(&MnemonicEntropy::new([0x42; 32]));
        let res = process_request(
            IpcRequest::UnlockOperator {
                mnemonic: wrong_mnemonic,
            },
            &state,
        )
        .await;
        assert!(matches!(
            res,
            IpcResponse::Error(message) if message.contains("ne correspond pas au Owner")
        ));

        // 4. Now is_operator_authorized must be true!
        assert!(is_operator_authorized(&state).await);
        assert!(state
            .operator_session
            .read()
            .await
            .as_ref()
            .expect("active operator session")
            .workspace
            .is_unlocked());
        let session_cancellation = state
            .operator_session
            .read()
            .await
            .as_ref()
            .expect("active operator session")
            .cancellation_token
            .clone();

        // 5. GetStatus reports is_operator_unlocked = true
        let status_res = process_request(IpcRequest::GetStatus, &state).await;
        if let IpcResponse::Status(s) = status_res {
            assert!(s.is_operator_unlocked);
        } else {
            panic!("Expected Status response");
        }

        // 6. LockOperator wipes workspace and locks
        let lock_res = process_request(IpcRequest::LockOperator, &state).await;
        assert_eq!(lock_res, IpcResponse::Success);
        assert!(!is_operator_authorized(&state).await);
        assert!(session_cancellation.is_cancelled());

        // 7. GetOperatorStatus reports is_unlocked = false
        let op_status = process_request(IpcRequest::GetOperatorStatus, &state).await;
        match op_status {
            IpcResponse::OperatorStatus(status) => {
                assert!(!status.is_unlocked);
                assert!(status.owner_id.is_none());
            }
            other => panic!("Expected OperatorStatus, got {other:?}"),
        }
    }
}
