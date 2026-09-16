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
    tracing::info!("Starting Windows Named Pipe IPC server at {}", pipe_name);

    let mut server = create_windows_pipe(&pipe_name, true)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Named pipe connection failed: {err}");
            server = create_windows_pipe(&pipe_name, false)?;
            continue;
        }

        let client = server;
        server = create_windows_pipe(&pipe_name, false)?;

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

    let mut server = create_windows_pipe(&pipe_name, true)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Terminal named pipe connection failed: {err}");
            server = create_windows_pipe(&pipe_name, false)?;
            continue;
        }

        let client = server;
        server = create_windows_pipe(&pipe_name, false)?;

        let state_clone = Arc::clone(&state);
        tokio::spawn(async move {
            if let Err(e) = handle_terminal_client(client, state_clone).await {
                tracing::debug!("Terminal client disconnected: {e}");
            }
        });
    }
}

/// Creates a local-only pipe that a desktop application can open even though the
/// service itself runs as LocalSystem. Windows' default pipe DACL otherwise only
/// grants access to the service account, making every non-elevated GUI look offline.
#[cfg(windows)]
fn create_windows_pipe(
    pipe_name: &str,
    first_instance: bool,
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

    // LocalSystem and administrators retain full control. Interactive desktop
    // users receive only the read/write access needed by the CLI and GUI.
    let sddl: Vec<u16> = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)"
        .encode_utf16()
        .chain(iter::once(0))
        .collect();
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
async fn run_unix_socket(state: Arc<IpcState>) -> Result<()> {
    use tokio::net::UnixListener;

    let path = state.config.ipc_endpoint();
    let _ = tokio::fs::remove_file(&path).await;
    if let Some(parent) = std::path::Path::new(&path).parent() {
        let _ = tokio::fs::create_dir_all(parent).await;
    }
    let listener = UnixListener::bind(&path)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = tokio::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o666)).await;
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
    }
    let listener = UnixListener::bind(&path)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = tokio::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o666)).await;
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
        IpcRequest::CloseTicket { peer, dial } => {
            if state.config.mode() != NodeMode::Operator {
                return IpcResponse::Error("Only operator can close tickets".to_string());
            }
            let target_peer: PeerId = match peer.parse() {
                Ok(p) => p,
                Err(e) => return IpcResponse::Error(format!("Invalid target PeerId: {e}")),
            };
            let target_dial: Option<libp2p::Multiaddr> = match dial {
                Some(d) => match d.parse() {
                    Ok(addr) => Some(addr),
                    Err(e) => {
                        return IpcResponse::Error(format!("Invalid target dial multiaddr: {e}"))
                    }
                },
                None => None,
            };

            if let Some(sender) = &state.p2p_sender {
                let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
                if sender
                    .send(fortiq_p2p::P2pCommand::CloseTicket {
                        peer: target_peer,
                        dial: target_dial,
                        reply: reply_tx,
                    })
                    .await
                    .is_ok()
                {
                    match tokio::time::timeout(std::time::Duration::from_secs(10), reply_rx).await {
                        Ok(Ok(Ok(()))) => IpcResponse::TicketClosed,
                        Ok(Ok(Err(err))) => {
                            IpcResponse::Error(format!("Failed to close remote ticket: {err}"))
                        }
                        Ok(Err(_)) => IpcResponse::Error(
                            "P2P event loop dropped ticket reply channel".to_string(),
                        ),
                        Err(_) => IpcResponse::Error(
                            "Timeout waiting for remote ticket closure".to_string(),
                        ),
                    }
                } else {
                    IpcResponse::Error("P2P subsystem channel closed".to_string())
                }
            } else {
                IpcResponse::Error("P2P subsystem not running".to_string())
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
    }
}

async fn handle_terminal_client<S>(stream: S, state: Arc<IpcState>) -> Result<()>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let (ipc_read_half, mut ipc_write) = tokio::io::split(stream);

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

    let forward_in = tokio::spawn(async move {
        while let Ok(Some(frame)) = fortiq_shell::ShellFrame::read_from(&mut ipc_read).await {
            if frame.write_to(&mut p2p_write).await.is_err() {
                break;
            }
        }
    });

    let forward_out = tokio::spawn(async move {
        while let Ok(Some(frame)) = fortiq_shell::ShellFrame::read_from(&mut p2p_read).await {
            if frame.write_to(&mut ipc_write).await.is_err() {
                break;
            }
        }
    });

    tokio::select! {
        _ = forward_in => {}
        _ = forward_out => {}
    }

    Ok(())
}
