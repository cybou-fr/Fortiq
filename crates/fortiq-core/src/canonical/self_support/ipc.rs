//! Local IPC Framing and Message Protocol for Self-Support Loopback.
//!
//! Provides length-delimited deterministic CBOR message framing over any
//! asynchronous duplex stream (Windows Named Pipes, Unix Domain Sockets,
//! or in-memory loopback duplex channels).

use serde::{Deserialize, Serialize};
use thiserror::Error;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

use crate::canonical::self_support::this_device::LocalDiagnostics;
use crate::canonical::shell::challenge::ShellAuthResponse;
use crate::canonical::types::{AccessEpoch, EntityId, TicketId};

/// Maximum allowed payload size for local IPC frames (4 MiB).
pub const MAX_LOCAL_IPC_FRAME_SIZE: usize = 4 * 1024 * 1024;

#[derive(Debug, Error)]
pub enum LocalIpcError {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("CBOR serialization error: {0}")]
    Cbor(String),
    #[error("Frame too large: {0} bytes (max {MAX_LOCAL_IPC_FRAME_SIZE})")]
    FrameTooLarge(usize),
    #[error("Connection unexpectedly closed")]
    ConnectionClosed,
    #[error("Protocol error: {0}")]
    Protocol(String),
}

/// Messages exchanged over the local IPC channel.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum LocalIpcMessage {
    /// Connectivity check.
    Ping,
    /// Connectivity response.
    Pong,
    /// Client initiation handshake.
    Handshake {
        client_id: EntityId,
        client_version: String,
    },
    /// Host handshake acknowledgment.
    HandshakeAck {
        device_id: EntityId,
        is_this_device: bool,
    },
    /// Query for local system diagnostics.
    DiagnosticsRequest,
    /// Diagnostics response from "This Device".
    DiagnosticsResponse(LocalDiagnostics),
    /// Request to create a self-support ticket on the local event graph.
    CreateSelfSupportTicket {
        title: String,
        description: String,
        creator_id: EntityId,
    },
    /// Response with the created self-support ticket details.
    SelfSupportTicketCreated {
        ticket_id: TicketId,
        initial_epoch: AccessEpoch,
    },
    /// Request to initiate a local shell session.
    ShellRequest {
        ticket_id: TicketId,
        epoch: AccessEpoch,
    },
    /// Challenge issued by host for shell authentication.
    ShellChallengePrompt {
        challenge: [u8; 32],
        epoch: AccessEpoch,
    },
    /// Client response to the shell challenge.
    ShellChallengeResponse { auth_response: ShellAuthResponse },
    /// Confirmation that local shell is authenticated and ready for streaming.
    ShellReady,
    /// Raw terminal byte stream frame.
    ShellStreamData(Vec<u8>),
    /// Instant client revocation signal.
    ShellRevoke { epoch: AccessEpoch, reason: String },
    /// Clean shell session termination.
    ShellClosed,
    /// Protocol or processing error.
    Error(String),
}

/// Asynchronous length-delimited framed transport for local IPC.
pub struct LocalIpcFramed<S> {
    stream: S,
}

impl<S> LocalIpcFramed<S> {
    pub fn new(stream: S) -> Self {
        Self { stream }
    }

    pub fn into_inner(self) -> S {
        self.stream
    }

    pub fn get_ref(&self) -> &S {
        &self.stream
    }

    pub fn get_mut(&mut self) -> &mut S {
        &mut self.stream
    }
}

impl<S: AsyncWrite + Unpin> LocalIpcFramed<S> {
    /// Serializes and sends a message with a 4-byte big-endian length prefix.
    pub async fn send_message(&mut self, message: &LocalIpcMessage) -> Result<(), LocalIpcError> {
        let mut payload = Vec::new();
        ciborium::into_writer(message, &mut payload)
            .map_err(|e| LocalIpcError::Cbor(e.to_string()))?;

        if payload.len() > MAX_LOCAL_IPC_FRAME_SIZE {
            return Err(LocalIpcError::FrameTooLarge(payload.len()));
        }

        let len = payload.len() as u32;
        self.stream.write_all(&len.to_be_bytes()).await?;
        self.stream.write_all(&payload).await?;
        self.stream.flush().await?;
        Ok(())
    }
}

impl<S: AsyncRead + Unpin> LocalIpcFramed<S> {
    /// Reads and deserializes the next length-delimited message.
    ///
    /// Returns `Ok(None)` if the connection reached EOF cleanly.
    pub async fn recv_message(&mut self) -> Result<Option<LocalIpcMessage>, LocalIpcError> {
        let mut len_buf = [0u8; 4];
        match self.stream.read_exact(&mut len_buf).await {
            Ok(_) => {}
            Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
            Err(e) => return Err(LocalIpcError::Io(e)),
        }

        let len = u32::from_be_bytes(len_buf) as usize;
        if len > MAX_LOCAL_IPC_FRAME_SIZE {
            return Err(LocalIpcError::FrameTooLarge(len));
        }

        let mut payload = vec![0u8; len];
        self.stream.read_exact(&mut payload).await?;

        let message: LocalIpcMessage =
            ciborium::from_reader(&payload[..]).map_err(|e| LocalIpcError::Cbor(e.to_string()))?;

        Ok(Some(message))
    }
}
