use std::sync::Arc;

use anyhow::Result;
use fortiq_core::{
    ipc::{DaemonStatus, IpcRequest, IpcResponse, PeerSummary},
    Config, NodeMode, TicketStore,
};
use libp2p::PeerId;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

pub struct IpcState {
    pub config: Config,
    pub peer_id: PeerId,
    pub listen_addresses: Vec<String>,
    pub ticket_store: TicketStore,
}

pub async fn run_ipc_server(state: Arc<IpcState>) -> Result<()> {
    #[cfg(windows)]
    {
        run_windows_pipe(state).await
    }
    #[cfg(unix)]
    {
        run_unix_socket(state).await
    }
}

#[cfg(windows)]
async fn run_windows_pipe(state: Arc<IpcState>) -> Result<()> {
    use tokio::net::windows::named_pipe::ServerOptions;

    let pipe_name = fortiq_core::ipc::DEFAULT_WINDOWS_PIPE_NAME;
    tracing::info!("Starting Windows Named Pipe IPC server at {}", pipe_name);

    let mut server = ServerOptions::new()
        .first_pipe_instance(true)
        .create(pipe_name)?;

    loop {
        if let Err(err) = server.connect().await {
            tracing::warn!("Named pipe connection failed: {err}");
            server = ServerOptions::new().create(pipe_name)?;
            continue;
        }

        let client = server;
        server = ServerOptions::new().create(pipe_name)?;

        let state_clone = Arc::clone(&state);
        tokio::spawn(async move {
            if let Err(e) = handle_client(client, state_clone).await {
                tracing::debug!("IPC client disconnected: {e}");
            }
        });
    }
}

#[cfg(unix)]
async fn run_unix_socket(state: Arc<IpcState>) -> Result<()> {
    use tokio::net::UnixListener;

    let path = std::env::var("FORTIQ_SOCK")
        .unwrap_or_else(|_| fortiq_core::ipc::DEFAULT_UNIX_SOCKET_PATH.to_owned());
    let _ = tokio::fs::remove_file(&path).await;
    if let Some(parent) = std::path::Path::new(&path).parent() {
        let _ = tokio::fs::create_dir_all(parent).await;
    }
    let listener = UnixListener::bind(&path)?;
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

async fn handle_client<S>(stream: S, state: Arc<IpcState>) -> Result<()>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
{
    let (read_half, mut write_half) = tokio::io::split(stream);
    let mut reader = BufReader::new(read_half);
    let mut line = String::new();

    while reader.read_line(&mut line).await? > 0 {
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
        line.clear();
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
        IpcRequest::CloseTicket { peer: _, dial: _ } => {
            if state.config.mode() != NodeMode::Operator {
                return IpcResponse::Error("Only operator can close tickets".to_string());
            }
            IpcResponse::TicketClosed
        }
        IpcRequest::ListPeers => IpcResponse::Peers(vec![PeerSummary {
            peer_id: "12D3KooWSx8m9zY24kLPq9aZ".to_string(),
            hostname: "OFFICE-PC-01".to_string(),
            os: "Windows 11 Pro".to_string(),
            transport: "QUIC DIRECT".to_string(),
            status: "Connected".to_string(),
        }]),
    }
}
