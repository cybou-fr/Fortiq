use std::io::Write;

use anyhow::{bail, Context, Result};
use clap::{Parser, Subcommand};
use fortiq_core::ipc::{DaemonStatus, IpcRequest, IpcResponse, TerminalSessionInit};
use fortiq_shell::ShellFrame;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};

mod ipc;

#[derive(Debug, Parser)]
#[command(
    name = "fortiq",
    version,
    about = "FORTIQ sovereign remote administration command-line client"
)]
struct Cli {
    /// Override IPC pipe/socket path
    #[arg(long, global = true)]
    pipe: Option<String>,

    /// Override terminal IPC pipe/socket path
    #[arg(long, global = true)]
    terminal_pipe: Option<String>,

    #[command(subcommand)]
    command: Commands,
}

#[derive(Debug, Subcommand)]
enum Commands {
    /// Query and display local FORTIQ daemon status.
    Status,

    /// Display local FORTIQ daemon PeerId and mode.
    Id,

    /// List discovered and active remote peers.
    Peers,

    /// Manage support session authorization tickets.
    Ticket {
        #[command(subcommand)]
        action: TicketCommand,
    },

    /// Open an interactive shell or execute a remote command on a peer.
    Shell {
        /// Target PeerId to connect to.
        peer: String,

        /// Optional direct QUIC multiaddress to dial (e.g. /ip4/x.x.x.x/udp/4001/quic-v1).
        #[arg(long)]
        dial: Option<String>,

        /// Run a single command non-interactively and exit.
        #[arg(long)]
        command: Option<String>,
    },
}

#[derive(Debug, Subcommand)]
enum TicketCommand {
    /// Show current local ticket state.
    Status,

    /// Open a new support ticket on a managed node.
    Open,

    /// Close an open ticket on a remote managed node (operator only).
    Close {
        /// Remote managed peer ID.
        peer: String,

        /// Optional direct QUIC multiaddress to dial.
        #[arg(long)]
        dial: Option<String>,
    },
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();
    let pipe = cli.pipe.as_deref();
    let term_pipe = cli.terminal_pipe.as_deref();

    match cli.command {
        Commands::Status => cmd_status(pipe).await,
        Commands::Id => cmd_id(pipe).await,
        Commands::Peers => cmd_peers(pipe).await,
        Commands::Ticket { action } => match action {
            TicketCommand::Status => cmd_ticket_status(pipe).await,
            TicketCommand::Open => cmd_ticket_open(pipe).await,
            TicketCommand::Close { peer, dial } => cmd_ticket_close(pipe, peer, dial).await,
        },
        Commands::Shell {
            peer,
            dial,
            command,
        } => cmd_shell(term_pipe, peer, dial, command).await,
    }
}

async fn get_daemon_status(pipe: Option<&str>) -> Result<DaemonStatus> {
    match ipc::send_command(&IpcRequest::GetStatus, pipe).await? {
        IpcResponse::Status(status) => Ok(status),
        IpcResponse::Error(err) => bail!("Daemon error: {err}"),
        _ => bail!("Unexpected response from daemon"),
    }
}

async fn cmd_status(pipe: Option<&str>) -> Result<()> {
    let status = get_daemon_status(pipe).await?;

    println!("{} {}\n", status.product, status.version);
    println!("Mode:       {}", status.mode);
    println!("PeerId:     {}", status.peer_id);
    println!("State:      {}", status.agent_state.to_uppercase());

    if let Some(op) = &status.authorized_operator {
        println!("Authorized: {op}");
    }

    match &status.active_ticket {
        Some(t) => println!("Ticket:     {:?} ({})", t.state, t.id),
        None => println!("Ticket:     NONE"),
    }

    println!("\nListen Addresses:");
    if status.listen_addresses.is_empty() {
        println!("  (none)");
    } else {
        for addr in &status.listen_addresses {
            println!("  - {addr}");
        }
    }

    Ok(())
}

async fn cmd_id(pipe: Option<&str>) -> Result<()> {
    let status = get_daemon_status(pipe).await?;
    println!("PeerId: {}", status.peer_id);
    println!("Mode:   {}", status.mode);
    Ok(())
}

async fn cmd_peers(pipe: Option<&str>) -> Result<()> {
    match ipc::send_command(&IpcRequest::ListPeers, pipe).await? {
        IpcResponse::Peers(peers) => {
            if peers.is_empty() {
                println!("No active or discovered peers.");
                return Ok(());
            }

            println!(
                "{:<54} {:<18} {:<10} {:<12} {:<10}",
                "PEER ID", "HOST", "OS", "TRANSPORT", "STATUS"
            );
            println!(
                "{:-<54} {:-<18} {:-<10} {:-<12} {:-<10}",
                "", "", "", "", ""
            );

            for p in peers {
                println!(
                    "{:<54} {:<18} {:<10} {:<12} {:<10}",
                    p.peer_id, p.hostname, p.os, p.transport, p.status
                );
            }
            Ok(())
        }
        IpcResponse::Error(err) => bail!("Daemon error: {err}"),
        _ => bail!("Unexpected response from daemon"),
    }
}

async fn cmd_ticket_status(pipe: Option<&str>) -> Result<()> {
    let status = get_daemon_status(pipe).await?;
    match status.active_ticket {
        Some(ticket) => {
            println!("Ticket ID:    {}", ticket.id);
            println!("Ticket State: {:?}", ticket.state);
        }
        None => {
            println!("No active ticket.");
        }
    }
    Ok(())
}

async fn cmd_ticket_open(pipe: Option<&str>) -> Result<()> {
    match ipc::send_command(&IpcRequest::OpenTicket, pipe).await? {
        IpcResponse::TicketOpened(ticket) => {
            println!("Ticket OPENED successfully.");
            println!("Ticket ID: {}", ticket.id);
            Ok(())
        }
        IpcResponse::Error(err) => bail!("Failed to open ticket: {err}"),
        _ => bail!("Unexpected response from daemon"),
    }
}

async fn cmd_ticket_close(pipe: Option<&str>, peer: String, dial: Option<String>) -> Result<()> {
    match ipc::send_command(&IpcRequest::CloseTicket { peer, dial }, pipe).await? {
        IpcResponse::TicketClosed => {
            println!("Ticket CLOSED successfully.");
            Ok(())
        }
        IpcResponse::Error(err) => bail!("Failed to close ticket: {err}"),
        _ => bail!("Unexpected response from daemon"),
    }
}

async fn cmd_shell(
    term_pipe: Option<&str>,
    peer: String,
    dial: Option<String>,
    command: Option<String>,
) -> Result<()> {
    let stream = ipc::connect_terminal(term_pipe).await?;
    let (read_half, mut write_half) = tokio::io::split(stream);

    let (cols, rows) = crossterm::terminal::size().unwrap_or((80, 24));
    let init = TerminalSessionInit {
        peer: peer.clone(),
        cols,
        rows,
        dial,
    };

    let mut init_bytes = serde_json::to_vec(&init)?;
    init_bytes.push(b'\n');
    write_half.write_all(&init_bytes).await?;
    write_half.flush().await?;

    let mut reader = BufReader::new(read_half);
    let mut status_line = String::new();
    reader
        .read_line(&mut status_line)
        .await
        .context("Failed to read terminal handshake response from daemon")?;

    #[derive(serde::Deserialize)]
    struct HandshakeResp {
        status: String,
        message: Option<String>,
    }

    let resp: HandshakeResp = serde_json::from_str(status_line.trim()).with_context(|| {
        format!(
            "Invalid terminal handshake from daemon: {}",
            status_line.trim()
        )
    })?;

    if resp.status != "ok" {
        let msg = resp
            .message
            .unwrap_or_else(|| "Terminal connection denied by daemon".into());
        bail!("{msg}");
    }

    let mut stream_read = reader;

    if let Some(cmd) = command {
        let cmd_payload = format!("{cmd}\r\nexit\r\n").into_bytes();
        ShellFrame::Data(cmd_payload)
            .write_to(&mut write_half)
            .await?;

        let mut stdout = tokio::io::stdout();
        while let Ok(Some(frame)) = ShellFrame::read_from(&mut stream_read).await {
            if let ShellFrame::Data(bytes) = frame {
                stdout.write_all(&bytes).await?;
                stdout.flush().await?;
            }
        }
        return Ok(());
    }

    // Interactive session: enable raw mode
    crossterm::terminal::enable_raw_mode().context("Failed to enable terminal raw mode")?;

    struct RawModeGuard;
    impl Drop for RawModeGuard {
        fn drop(&mut self) {
            let _ = crossterm::terminal::disable_raw_mode();
        }
    }
    let _guard = RawModeGuard;

    let mut stdin = tokio::io::stdin();
    let mut stdout = tokio::io::stdout();

    let (frame_tx, mut frame_rx) = tokio::sync::mpsc::channel::<ShellFrame>(128);
    let frame_tx_pong = frame_tx.clone();

    let send_task = tokio::spawn(async move {
        while let Some(frame) = frame_rx.recv().await {
            if frame.write_to(&mut write_half).await.is_err() {
                break;
            }
        }
    });

    let stdin_task = tokio::spawn(async move {
        let mut buf = [0u8; 1024];
        loop {
            match stdin.read(&mut buf).await {
                Ok(0) => break,
                Ok(n) => {
                    if frame_tx
                        .send(ShellFrame::Data(buf[..n].to_vec()))
                        .await
                        .is_err()
                    {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });

    let recv_task = tokio::spawn(async move {
        while let Ok(Some(frame)) = ShellFrame::read_from(&mut stream_read).await {
            match frame {
                ShellFrame::Data(bytes) => {
                    if stdout.write_all(&bytes).await.is_err() || stdout.flush().await.is_err() {
                        break;
                    }
                }
                ShellFrame::Ping => {
                    let _ = frame_tx_pong.send(ShellFrame::Pong).await;
                }
                ShellFrame::Pong | ShellFrame::Resize { .. } => {}
            }
        }
    });

    tokio::select! {
        _ = recv_task => {}
        _ = stdin_task => {}
    }

    drop(_guard);
    println!("\r\n[FORTIQ session closed]");
    let _ = std::io::stdout().flush();
    send_task.abort();

    Ok(())
}
