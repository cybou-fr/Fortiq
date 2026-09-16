use anyhow::{Context, Result};
use fortiq_core::ipc::{IpcRequest, IpcResponse};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};

const MAX_IPC_LINE_BYTES: usize = 64 * 1024;

#[cfg(windows)]
pub type TerminalStream = tokio::net::windows::named_pipe::NamedPipeClient;

#[cfg(unix)]
pub type TerminalStream = tokio::net::UnixStream;

pub async fn send_command(req: &IpcRequest, endpoint: Option<&str>) -> Result<IpcResponse> {
    #[cfg(windows)]
    let client = {
        use tokio::net::windows::named_pipe::ClientOptions;
        let pipe_name = endpoint
            .map(|s| s.to_string())
            .unwrap_or_else(fortiq_core::ipc::windows_pipe_name);
        ClientOptions::new().open(&pipe_name).with_context(|| {
            format!("Cannot connect to FORTIQ service via Named Pipe ({pipe_name}). Is fortiq-service running?")
        })?
    };

    #[cfg(unix)]
    let client = {
        let path = endpoint
            .map(|s| s.to_string())
            .unwrap_or_else(fortiq_core::ipc::unix_socket_path);
        tokio::net::UnixStream::connect(&path).await.with_context(|| {
            format!("Cannot connect to FORTIQ service via Unix Socket ({path}). Is fortiq-service running?")
        })?
    };

    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Platform not supported for local IPC");

    let (read_half, mut write_half) = tokio::io::split(client);
    let mut reader = BufReader::new(read_half);

    let mut req_bytes = serde_json::to_vec(req)?;
    req_bytes.push(b'\n');
    write_half.write_all(&req_bytes).await?;
    write_half.flush().await?;

    let mut line = String::new();
    let bytes_read = {
        let mut limiter = (&mut reader).take((MAX_IPC_LINE_BYTES + 1) as u64);
        limiter.read_line(&mut line).await?
    };

    if bytes_read == 0 {
        anyhow::bail!("FORTIQ service closed the IPC connection unexpectedly");
    }
    if bytes_read > MAX_IPC_LINE_BYTES {
        anyhow::bail!("IPC response exceeded maximum limit (64 KB)");
    }

    let resp: IpcResponse = serde_json::from_str(line.trim())
        .with_context(|| format!("Failed to parse daemon IPC response: {}", line.trim()))?;
    Ok(resp)
}

pub async fn connect_terminal(endpoint: Option<&str>) -> Result<TerminalStream> {
    #[cfg(windows)]
    {
        use tokio::net::windows::named_pipe::ClientOptions;
        let pipe_name = endpoint
            .map(|s| s.to_string())
            .unwrap_or_else(fortiq_core::ipc::windows_terminal_pipe_name);
        let client = ClientOptions::new().open(&pipe_name).with_context(|| {
            format!(
                "Cannot connect to FORTIQ terminal pipe ({pipe_name}). Is fortiq-service running?"
            )
        })?;
        Ok(client)
    }

    #[cfg(unix)]
    {
        let path = endpoint
            .map(|s| s.to_string())
            .unwrap_or_else(fortiq_core::ipc::unix_terminal_socket_path);
        let stream = tokio::net::UnixStream::connect(&path)
            .await
            .with_context(|| {
                format!(
                    "Cannot connect to FORTIQ terminal socket ({path}). Is fortiq-service running?"
                )
            })?;
        Ok(stream)
    }

    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Platform not supported for terminal IPC")
}
