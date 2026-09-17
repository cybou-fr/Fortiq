use std::sync::Arc;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::mpsc;
use tracing::info;
use vte::Parser;

use fortiq_core::ipc::TerminalSessionInit;
use fortiq_shell::ShellFrame;

use super::parser::TerminalPerformer;
use super::screen::TerminalScreen;
use crate::ipc::{IpcClient, IpcClientError};

#[derive(Debug)]
pub enum TerminalCommand {
    Input(Vec<u8>),
    Resize { cols: u16, rows: u16 },
    Close,
}

pub struct TerminalSession;

impl TerminalSession {
    pub async fn spawn(
        ipc: Arc<IpcClient>,
        peer: String,
        ticket_id: Option<String>,
        cols: u16,
        rows: u16,
        cmd_rx: mpsc::Receiver<TerminalCommand>,
        text_update_tx: mpsc::Sender<String>,
    ) -> Result<(), IpcClientError> {
        let stream = ipc.connect_terminal().await?;
        Self::run_with_stream(stream, peer, ticket_id, cols, rows, cmd_rx, text_update_tx).await
    }

    pub async fn run_with_stream<S>(
        stream: S,
        peer: String,
        ticket_id: Option<String>,
        cols: u16,
        rows: u16,
        mut cmd_rx: mpsc::Receiver<TerminalCommand>,
        text_update_tx: mpsc::Sender<String>,
    ) -> Result<(), IpcClientError>
    where
        S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
    {
        let (read_half, mut write_half) = tokio::io::split(stream);

        let init = TerminalSessionInit {
            peer,
            ticket_id,
            cols,
            rows,
            dial: None,
        };

        let mut init_bytes = serde_json::to_vec(&init)?;
        init_bytes.push(b'\n');
        write_half.write_all(&init_bytes).await?;
        write_half.flush().await?;

        let mut reader = BufReader::new(read_half);
        let mut status_line = String::new();
        reader.read_line(&mut status_line).await?;

        #[derive(serde::Deserialize)]
        struct HandshakeResp {
            status: String,
            message: Option<String>,
        }

        let resp: HandshakeResp = serde_json::from_str(status_line.trim())
            .map_err(|e| IpcClientError::DaemonError(format!("Invalid terminal handshake: {e}")))?;

        if resp.status != "ok" {
            let msg = resp.message.unwrap_or_else(|| "Connection denied".into());
            return Err(IpcClientError::DaemonError(msg));
        }

        let (control_tx, mut control_rx) = mpsc::channel::<ShellFrame>(32);
        let (local_resize_tx, mut local_resize_rx) = mpsc::channel::<(u16, u16)>(16);

        // Spawn writer task
        let writer_handle = tokio::spawn(async move {
            loop {
                tokio::select! {
                    Some(frame) = control_rx.recv() => {
                        if frame.write_to(&mut write_half).await.is_err() {
                            break;
                        }
                    }
                    cmd = cmd_rx.recv() => {
                        match cmd {
                            Some(TerminalCommand::Input(bytes)) => {
                                let frame = ShellFrame::Data(bytes);
                                if frame.write_to(&mut write_half).await.is_err() {
                                    break;
                                }
                            }
                            Some(TerminalCommand::Resize { cols, rows }) => {
                                let _ = local_resize_tx.send((cols, rows)).await;
                                let frame = ShellFrame::Resize { cols, rows };
                                if frame.write_to(&mut write_half).await.is_err() {
                                    break;
                                }
                            }
                            Some(TerminalCommand::Close) | None => {
                                break;
                            }
                        }
                    }
                }
            }
        });

        // Reader loop
        tokio::spawn(async move {
            let mut stream_read = reader;
            let mut screen = TerminalScreen::new(cols, rows);
            let mut parser = Parser::new();

            loop {
                tokio::select! {
                    Some((new_cols, new_rows)) = local_resize_rx.recv() => {
                        screen.resize(new_cols, new_rows);
                        let text = screen.render_plain_text();
                        if text_update_tx.send(text).await.is_err() {
                            break;
                        }
                    }
                    frame_res = ShellFrame::read_from(&mut stream_read) => {
                        match frame_res {
                            Ok(Some(frame)) => {
                                match frame {
                                    ShellFrame::Data(bytes) => {
                                        {
                                            let mut performer = TerminalPerformer::new(&mut screen);
                                            parser.advance(&mut performer, &bytes);
                                        }
                                        let text = screen.render_plain_text();
                                        if text_update_tx.send(text).await.is_err() {
                                            break;
                                        }
                                    }
                                    ShellFrame::Ping => {
                                        // Immediately send Pong back via writer
                                        let _ = control_tx.send(ShellFrame::Pong).await;
                                    }
                                    ShellFrame::Pong => {}
                                    ShellFrame::Resize { cols: r_cols, rows: r_rows } => {
                                        screen.resize(r_cols, r_rows);
                                        let text = screen.render_plain_text();
                                        if text_update_tx.send(text).await.is_err() {
                                            break;
                                        }
                                    }
                                }
                            }
                            _ => break,
                        }
                    }
                }
            }

            info!("Terminal read loop terminated");
            writer_handle.abort();
        });

        Ok(())
    }
}
