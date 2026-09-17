use std::path::{Path, PathBuf};
use fortiq_core::ipc::{IpcRequest, IpcResponse};
use thiserror::Error;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};

pub const MAX_IPC_LINE_BYTES: usize = 64 * 1024;

#[derive(Debug, Error)]
pub enum IpcClientError {
    #[error("Cannot connect to FORTIQ service ({endpoint}): {source}")]
    ConnectionFailed {
        endpoint: String,
        #[source]
        source: std::io::Error,
    },
    #[error("Service closed IPC connection unexpectedly")]
    ConnectionClosed,
    #[error("IPC response exceeded maximum limit (64 KB)")]
    ResponseTooLarge,
    #[error("Failed to parse IPC JSON: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("Daemon returned error: {0}")]
    DaemonError(String),
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),
}

#[cfg(windows)]
pub type TerminalStream = tokio::net::windows::named_pipe::NamedPipeClient;

#[cfg(unix)]
pub type TerminalStream = tokio::net::UnixStream;

#[derive(Debug, Clone, Default)]
pub struct IpcClient {
    custom_endpoint: Option<String>,
    custom_term_endpoint: Option<String>,
}

impl IpcClient {
    pub fn new(custom_endpoint: Option<String>, custom_term_endpoint: Option<String>) -> Self {
        Self {
            custom_endpoint,
            custom_term_endpoint,
        }
    }

    pub fn endpoint(&self) -> String {
        #[cfg(windows)]
        {
            self.custom_endpoint
                .clone()
                .unwrap_or_else(fortiq_core::ipc::windows_pipe_name)
        }
        #[cfg(unix)]
        {
            self.custom_endpoint
                .clone()
                .unwrap_or_else(fortiq_core::ipc::unix_socket_path)
        }
    }

    pub fn terminal_endpoint(&self) -> String {
        #[cfg(windows)]
        {
            self.custom_term_endpoint
                .clone()
                .unwrap_or_else(fortiq_core::ipc::windows_terminal_pipe_name)
        }
        #[cfg(unix)]
        {
            self.custom_term_endpoint
                .clone()
                .unwrap_or_else(fortiq_core::ipc::unix_terminal_socket_path)
        }
    }

    pub async fn send_request(&self, req: &IpcRequest) -> Result<IpcResponse, IpcClientError> {
        let endpoint = self.endpoint();

        #[cfg(windows)]
        let client = {
            use tokio::net::windows::named_pipe::ClientOptions;
            ClientOptions::new()
                .open(&endpoint)
                .map_err(|e| IpcClientError::ConnectionFailed {
                    endpoint: endpoint.clone(),
                    source: e,
                })?
        };

        #[cfg(unix)]
        let client = {
            tokio::net::UnixStream::connect(&endpoint)
                .await
                .map_err(|e| IpcClientError::ConnectionFailed {
                    endpoint: endpoint.clone(),
                    source: e,
                })?
        };

        #[cfg(not(any(windows, unix)))]
        {
            return Err(IpcClientError::DaemonError(
                "Platform unsupported for IPC".into(),
            ));
        }

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
            return Err(IpcClientError::ConnectionClosed);
        }
        if bytes_read > MAX_IPC_LINE_BYTES {
            return Err(IpcClientError::ResponseTooLarge);
        }

        let resp: IpcResponse = serde_json::from_str(line.trim())?;
        match resp {
            IpcResponse::Error(msg) => Err(IpcClientError::DaemonError(msg)),
            valid => Ok(valid),
        }
    }

    pub async fn connect_terminal(&self) -> Result<TerminalStream, IpcClientError> {
        let term_endpoint = self.terminal_endpoint();

        #[cfg(windows)]
        {
            use tokio::net::windows::named_pipe::ClientOptions;
            ClientOptions::new()
                .open(&term_endpoint)
                .map_err(|e| IpcClientError::ConnectionFailed {
                    endpoint: term_endpoint.clone(),
                    source: e,
                })
        }

        #[cfg(unix)]
        {
            tokio::net::UnixStream::connect(&term_endpoint)
                .await
                .map_err(|e| IpcClientError::ConnectionFailed {
                    endpoint: term_endpoint.clone(),
                    source: e,
                })
        }

        #[cfg(not(any(windows, unix)))]
        {
            Err(IpcClientError::DaemonError(
                "Platform unsupported for terminal IPC".into(),
            ))
        }
    }

    pub fn stage_file(source: &Path) -> Result<PathBuf, IpcClientError> {
        let staging_path = fortiq_core::ipc::new_upload_staging_path(source)?;
        std::fs::copy(source, &staging_path)?;
        Ok(staging_path)
    }
}
