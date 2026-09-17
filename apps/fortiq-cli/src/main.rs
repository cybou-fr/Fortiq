use std::io::Write;

use anyhow::{bail, Context, Result};
use clap::{Parser, Subcommand};
use fortiq_core::{
    ipc::{DaemonStatus, IpcRequest, IpcResponse, TerminalSessionInit},
    TicketPriority, TicketState,
};
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

    /// List support tickets.
    Tickets {
        /// Optional state filter (OPEN, IN_PROGRESS, RESOLVED, CLOSED)
        #[arg(long)]
        state: Option<String>,
    },

    /// Manage support tickets, chat, files, and remote access.
    Ticket {
        #[command(subcommand)]
        action: TicketCommand,
    },

    /// Open an interactive shell or execute a remote command on a peer within a ticket.
    Shell {
        /// Target PeerId to connect to.
        peer: String,

        /// Specific ticket ID to bind the shell session to.
        #[arg(long)]
        ticket_id: Option<String>,

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
    /// List support tickets.
    List {
        /// Optional state filter (OPEN, IN_PROGRESS, RESOLVED, CLOSED)
        #[arg(long)]
        state: Option<String>,
    },

    /// Show full details of a specific ticket.
    Show {
        /// Ticket ID (e.g. FTQ-...)
        ticket_id: String,
    },

    /// Create a new support ticket.
    Create {
        /// Title of the ticket.
        #[arg(long)]
        title: String,

        /// Description of the issue.
        #[arg(long, default_value = "")]
        description: String,

        /// Priority: NORMAL, HIGH, or URGENT.
        #[arg(long, default_value = "NORMAL")]
        priority: String,
    },

    /// Send a chat message within a ticket.
    Message {
        /// Ticket ID.
        ticket_id: String,

        /// Message body text.
        body: String,
    },

    /// Send a file attached to a ticket.
    SendFile {
        /// Ticket ID.
        ticket_id: String,

        /// Path to the file to send.
        path: std::path::PathBuf,
    },

    /// Toggle remote access permission for a ticket.
    Access {
        /// Ticket ID.
        ticket_id: String,

        /// enable (true) or disable (false).
        #[arg(value_parser = clap::builder::BoolishValueParser::new())]
        enabled: bool,
    },

    /// Update ticket state (OPEN, IN_PROGRESS, RESOLVED, CLOSED).
    SetStatus {
        /// Ticket ID.
        ticket_id: String,

        /// New state: OPEN, IN_PROGRESS, RESOLVED, or CLOSED.
        state: String,
    },

    /// Show current local active ticket (legacy compatibility).
    Status,

    /// Open a new support ticket on a managed node (legacy compatibility).
    Open,
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
        Commands::Tickets { state } => cmd_tickets_list(pipe, state).await,
        Commands::Ticket { action } => match action {
            TicketCommand::List { state } => cmd_tickets_list(pipe, state).await,
            TicketCommand::Show { ticket_id } => cmd_ticket_show(pipe, &ticket_id).await,
            TicketCommand::Create {
                title,
                description,
                priority,
            } => cmd_ticket_create(pipe, &title, &description, &priority).await,
            TicketCommand::Message { ticket_id, body } => {
                cmd_ticket_message(pipe, &ticket_id, &body).await
            }
            TicketCommand::SendFile { ticket_id, path } => {
                cmd_ticket_send_file(pipe, &ticket_id, &path).await
            }
            TicketCommand::Access { ticket_id, enabled } => {
                cmd_ticket_access(pipe, &ticket_id, enabled).await
            }
            TicketCommand::SetStatus { ticket_id, state } => {
                cmd_ticket_set_status(pipe, &ticket_id, &state).await
            }
            TicketCommand::Status => cmd_ticket_status(pipe).await,
            TicketCommand::Open => cmd_ticket_open(pipe).await,
        },
        Commands::Shell {
            peer,
            ticket_id,
            dial,
            command,
        } => cmd_shell(term_pipe, peer, ticket_id, dial, command).await,
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

async fn cmd_tickets_list(pipe: Option<&str>, state_filter: Option<String>) -> Result<()> {
    let filter = state_filter.and_then(|s| TicketState::parse_str(&s.to_uppercase()));
    match ipc::send_command(
        &IpcRequest::ListTickets {
            state_filter: filter,
        },
        pipe,
    )
    .await?
    {
        IpcResponse::Tickets(tickets) => {
            if tickets.is_empty() {
                println!("Aucun ticket trouvé.");
                return Ok(());
            }

            println!(
                "{:<16} {:<12} {:<8} {:<15} {:<32}",
                "ID", "STATUT", "PRIORITÉ", "ACCÈS DISTANT", "TITRE"
            );
            println!("{:-<16} {:-<12} {:-<8} {:-<15} {:-<32}", "", "", "", "", "");

            for t in tickets {
                let access = if t.remote_access_enabled {
                    "ACTIVÉ"
                } else {
                    "DÉSACTIVÉ"
                };
                println!(
                    "{:<16} {:<12} {:<8} {:<15} {:<32}",
                    t.id,
                    t.state.as_str(),
                    t.priority.as_str(),
                    access,
                    t.title
                );
            }
            Ok(())
        }
        IpcResponse::Error(err) => bail!("Erreur démon: {err}"),
        _ => bail!("Réponse inattendue du démon"),
    }
}

async fn cmd_ticket_show(pipe: Option<&str>, ticket_id: &str) -> Result<()> {
    match ipc::send_command(
        &IpcRequest::GetTicket {
            ticket_id: ticket_id.to_string(),
        },
        pipe,
    )
    .await?
    {
        IpcResponse::TicketDetail(Some(detail)) => {
            let t = &detail.ticket;
            println!("============================================================");
            println!("Ticket:        {}", t.id);
            println!("Titre:         {}", t.title);
            if !t.description.is_empty() {
                println!("Description:   {}", t.description);
            }
            println!("Statut:        {}", t.state.as_str());
            println!("Priorité:      {}", t.priority.as_str());
            println!(
                "Accès distant: {}",
                if t.remote_access_enabled {
                    "ACTIVÉ"
                } else {
                    "DÉSACTIVÉ"
                }
            );
            println!("Client:        {}", t.client_peer_id);
            println!("Opérateur:     {}", t.operator_peer_id);
            println!("Créé le:       {} (timestamp)", t.created_at);
            if let Some(closed) = t.closed_at {
                println!("Fermé le:      {} (timestamp)", closed);
            }
            println!("============================================================");

            println!("\n--- Messages ({}) ---", detail.messages.len());
            for m in &detail.messages {
                println!("[{}] {}: {}", m.delivery_state, m.sender_peer_id, m.body);
            }

            println!("\n--- Fichiers joints ({}) ---", detail.attachments.len());
            for a in &detail.attachments {
                println!(
                    "- {} ({} octets, SHA256: {}) -> {}",
                    a.filename, a.size_bytes, a.sha256, a.local_path
                );
            }

            println!("\n--- Sessions Shell ({}) ---", detail.shell_sessions.len());
            for s in &detail.shell_sessions {
                println!(
                    "Session {} par {} ({}) - Résultat: {:?}",
                    s.id, s.operator_peer_id, s.transport, s.result
                );
            }

            println!("\n--- Journal d'activité ({}) ---", detail.events.len());
            for e in &detail.events {
                println!(
                    "[{}] {}: {}",
                    e.timestamp,
                    e.kind,
                    e.metadata.as_deref().unwrap_or("")
                );
            }
            Ok(())
        }
        IpcResponse::TicketDetail(None) => bail!("Ticket introuvable: {ticket_id}"),
        IpcResponse::Error(err) => bail!("Erreur démon: {err}"),
        _ => bail!("Réponse inattendue du démon"),
    }
}

async fn cmd_ticket_create(
    pipe: Option<&str>,
    title: &str,
    description: &str,
    priority_str: &str,
) -> Result<()> {
    let priority = TicketPriority::parse_str(&priority_str.to_uppercase());
    match ipc::send_command(
        &IpcRequest::CreateTicket {
            title: title.to_string(),
            description: description.to_string(),
            priority,
        },
        pipe,
    )
    .await?
    {
        IpcResponse::TicketCreated(ticket) => {
            println!("Ticket créé avec succès !");
            println!("ID:            {}", ticket.id);
            println!("Titre:         {}", ticket.title);
            println!("Statut:        {}", ticket.state.as_str());
            println!("Priorité:      {}", ticket.priority.as_str());
            println!(
                "Accès distant: {}",
                if ticket.remote_access_enabled {
                    "ACTIVÉ"
                } else {
                    "DÉSACTIVÉ"
                }
            );
            Ok(())
        }
        IpcResponse::Error(err) => bail!("Échec de création du ticket: {err}"),
        _ => bail!("Réponse inattendue du démon"),
    }
}

async fn cmd_ticket_message(pipe: Option<&str>, ticket_id: &str, body: &str) -> Result<()> {
    match ipc::send_command(
        &IpcRequest::SendChatMessage {
            ticket_id: ticket_id.to_string(),
            body: body.to_string(),
        },
        pipe,
    )
    .await?
    {
        IpcResponse::MessageSent(msg) => {
            println!("Message envoyé avec succès !");
            println!("ID:      {}", msg.id);
            println!("Statut:  {}", msg.delivery_state);
            Ok(())
        }
        IpcResponse::Error(err) => bail!("Échec d'envoi du message: {err}"),
        _ => bail!("Réponse inattendue du démon"),
    }
}

async fn cmd_ticket_send_file(
    pipe: Option<&str>,
    ticket_id: &str,
    path: &std::path::Path,
) -> Result<()> {
    if !path.exists() {
        bail!("Fichier introuvable: {}", path.display());
    }
    let staged_path = fortiq_core::ipc::new_upload_staging_path(path)?;
    tokio::fs::copy(path, &staged_path)
        .await
        .with_context(|| format!("Impossible de préparer {}", path.display()))?;
    let response = ipc::send_command(
        &IpcRequest::SendFile {
            ticket_id: ticket_id.to_string(),
            staged_path: staged_path.to_string_lossy().to_string(),
        },
        pipe,
    )
    .await;
    let response = match response {
        Ok(response) => response,
        Err(error) => {
            let _ = tokio::fs::remove_file(&staged_path).await;
            return Err(error);
        }
    };
    match response {
        IpcResponse::FileSent(att) => {
            println!("Fichier transféré avec succès !");
            println!("Nom:     {}", att.filename);
            println!("Taille:  {} octets", att.size_bytes);
            println!("SHA256:  {}", att.sha256);
            println!("ID:      {}", att.id);
            Ok(())
        }
        IpcResponse::Error(err) => {
            let _ = tokio::fs::remove_file(&staged_path).await;
            bail!("Échec du transfert de fichier: {err}")
        }
        _ => {
            let _ = tokio::fs::remove_file(&staged_path).await;
            bail!("Réponse inattendue du démon")
        }
    }
}

async fn cmd_ticket_access(pipe: Option<&str>, ticket_id: &str, enabled: bool) -> Result<()> {
    match ipc::send_command(
        &IpcRequest::SetRemoteAccess {
            ticket_id: ticket_id.to_string(),
            enabled,
        },
        pipe,
    )
    .await?
    {
        IpcResponse::TicketUpdated(Some(ticket)) => {
            println!(
                "Accès à distance {} pour le ticket {}.",
                if ticket.remote_access_enabled {
                    "ACTIVÉ"
                } else {
                    "DÉSACTIVÉ"
                },
                ticket.id
            );
            Ok(())
        }
        IpcResponse::TicketUpdated(None) => bail!("Ticket introuvable: {ticket_id}"),
        IpcResponse::Error(err) => bail!("Échec: {err}"),
        _ => bail!("Réponse inattendue du démon"),
    }
}

async fn cmd_ticket_set_status(pipe: Option<&str>, ticket_id: &str, state_str: &str) -> Result<()> {
    let state = TicketState::parse_str(&state_str.to_uppercase()).ok_or_else(|| {
        anyhow::anyhow!(
            "Statut invalide: {state_str}. Valeurs acceptées: OPEN, IN_PROGRESS, RESOLVED, CLOSED"
        )
    })?;
    match ipc::send_command(
        &IpcRequest::UpdateTicketStatus {
            ticket_id: ticket_id.to_string(),
            state,
        },
        pipe,
    )
    .await?
    {
        IpcResponse::TicketUpdated(Some(ticket)) => {
            println!(
                "Statut du ticket {} mis à jour: {}.",
                ticket.id,
                ticket.state.as_str()
            );
            Ok(())
        }
        IpcResponse::TicketUpdated(None) => bail!("Ticket introuvable: {ticket_id}"),
        IpcResponse::Error(err) => bail!("Échec: {err}"),
        _ => bail!("Réponse inattendue du démon"),
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

async fn cmd_shell(
    term_pipe: Option<&str>,
    peer: String,
    ticket_id: Option<String>,
    dial: Option<String>,
    command: Option<String>,
) -> Result<()> {
    let stream = ipc::connect_terminal(term_pipe).await?;
    let (read_half, mut write_half) = tokio::io::split(stream);

    let (cols, rows) = crossterm::terminal::size().unwrap_or((80, 24));
    let init = TerminalSessionInit {
        peer: peer.clone(),
        ticket_id,
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
            match frame {
                ShellFrame::Data(bytes) => {
                    stdout.write_all(&bytes).await?;
                    stdout.flush().await?;
                }
                ShellFrame::Ping => {
                    ShellFrame::Pong.write_to(&mut write_half).await?;
                }
                ShellFrame::Pong | ShellFrame::Resize { .. } => {}
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
