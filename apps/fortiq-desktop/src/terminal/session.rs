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
        mut cmd_rx: mpsc::Receiver<TerminalCommand>,
        text_update_tx: mpsc::Sender<String>,
    ) -> Result<(), IpcClientError> {
        let stream = ipc.connect_terminal().await?;
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

        // Spawn writer task
        let writer_handle = tokio::spawn(async move {
            while let Some(cmd) = cmd_rx.recv().await {
                match cmd {
                    TerminalCommand::Input(bytes) => {
                        let frame = ShellFrame::Data(bytes);
                        if frame.write_to(&mut write_half).await.is_err() {
                            break;
                        }
                    }
                    TerminalCommand::Resize { cols, rows } => {
                        let frame = ShellFrame::Resize { cols, rows };
                        if frame.write_to(&mut write_half).await.is_err() {
                            break;
                        }
                    }
                    TerminalCommand::Close => {
                        break;
                    }
                }
            }
        });

        // Reader loop
        tokio::spawn(async move {
            let mut stream_read = reader;
            let mut screen = TerminalScreen::new(cols, rows);
            let mut parser = Parser::new();

            while let Ok(Some(frame)) = ShellFrame::read_from(&mut stream_read).await {
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
                        // Handled automatically or ignored
                    }
                    ShellFrame::Pong => {}
                    ShellFrame::Resize { .. } => {}
                }
            }

            info!("Terminal read loop terminated");
            writer_handle.abort();
        });

        Ok(())
    }
}
