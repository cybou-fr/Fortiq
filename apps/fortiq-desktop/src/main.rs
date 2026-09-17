use std::sync::Arc;
use slint::ComponentHandle;
use tokio::sync::mpsc;
use tracing::info;

use fortiq_desktop::backend::BackendActor;
use fortiq_desktop::command::DesktopCommand;
use fortiq_desktop::event::DesktopEvent;
use fortiq_desktop::file_picker::{FilePicker, NativeFilePicker};
use fortiq_desktop::instance_lock::InstanceLock;
use fortiq_desktop::ipc::IpcClient;
use fortiq_desktop::settings::DesktopSettings;
use fortiq_desktop::{
    AppWindow, AttachmentItem, ChatMessageItem, PeerItem, TicketItem,
};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    // 1. Single Instance Check
    let _lock = match InstanceLock::acquire() {
        Ok(l) => l,
        Err(e) => {
            eprintln!("[FORTIQ-DESKTOP] Single instance violation: {e}");
            return Ok(());
        }
    };

    // 2. Logging Setup
    let _logger = fortiq_desktop::logging::init();
    info!("FORTIQ Desktop starting up");

    // 3. Settings
    let settings = DesktopSettings::load();

    // 4. Tokio Runtime Setup
    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()?;

    let (cmd_tx, cmd_rx) = mpsc::channel::<DesktopCommand>(128);
    let (event_tx, mut event_rx) = mpsc::channel::<DesktopEvent>(128);

    let ipc_endpoint = settings.daemon_pipe.clone();
    let ipc = Arc::new(IpcClient::new(ipc_endpoint, None));

    let backend = BackendActor::new(ipc, cmd_rx, event_tx);
    rt.spawn(backend.run());

    // 5. Initialize Slint UI
    let app = AppWindow::new()?;
    let ui_weak = app.as_weak();

    // Set initial settings into UI
    app.set_daemon_pipe(settings.daemon_pipe.unwrap_or_default().into());
    app.set_minimize_to_tray(settings.minimize_to_tray);

    // Bind UI Callbacks
    {
        let tx = cmd_tx.clone();
        app.on_refresh(move || {
            let _ = tx.blocking_send(DesktopCommand::Refresh);
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_select_ticket(move |id| {
            let _ = tx.blocking_send(DesktopCommand::SelectTicket(id.to_string()));
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_create_ticket(move |title, prio| {
            let _ = tx.blocking_send(DesktopCommand::CreateTicket {
                title: title.to_string(),
                priority: prio as u8,
            });
        });
    }

    {
        let tx = cmd_tx.clone();
        let ui_handle = app.as_weak();
        app.on_send_message(move |text| {
            if let Some(ui) = ui_handle.upgrade() {
                let ticket_id = ui.get_selected_ticket_id().to_string();
                let _ = tx.blocking_send(DesktopCommand::SendMessage {
                    ticket_id,
                    body: text.to_string(),
                });
            }
        });
    }

    {
        let tx = cmd_tx.clone();
        let ui_handle = app.as_weak();
        let picker = NativeFilePicker;
        app.on_pick_and_send_file(move || {
            if let Some(path) = picker.pick_file() {
                if let Some(ui) = ui_handle.upgrade() {
                    let ticket_id = ui.get_selected_ticket_id().to_string();
                    let _ = tx.blocking_send(DesktopCommand::SendFile {
                        ticket_id,
                        path,
                    });
                }
            }
        });
    }

    {
        let tx = cmd_tx.clone();
        let ui_handle = app.as_weak();
        app.on_start_shell(move || {
            if let Some(ui) = ui_handle.upgrade() {
                let ticket_id = ui.get_selected_ticket_id().to_string();
                let _ = tx.blocking_send(DesktopCommand::StartShell {
                    ticket_id,
                    cols: 80,
                    rows: 24,
                });
            }
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_close_shell(move || {
            let _ = tx.blocking_send(DesktopCommand::CloseShell);
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_revoke_shell(move || {
            let _ = tx.blocking_send(DesktopCommand::RevokeShell);
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_send_terminal_input(move |text| {
            let _ = tx.blocking_send(DesktopCommand::ShellInput(text.as_bytes().to_vec()));
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_unlock_operator(move |mnemonic| {
            let _ = tx.blocking_send(DesktopCommand::UnlockOperator(mnemonic.to_string()));
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_lock_operator(move || {
            let _ = tx.blocking_send(DesktopCommand::LockOperator);
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_run_diagnostic(move || {
            let _ = tx.blocking_send(DesktopCommand::RefreshSelfSupport);
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_repair_database(move || {
            let _ = tx.blocking_send(DesktopCommand::TriggerSelfSupportAction("repair_db".into()));
        });
    }

    {
        let tx = cmd_tx.clone();
        app.on_reset_peers(move || {
            let _ = tx.blocking_send(DesktopCommand::TriggerSelfSupportAction("reset_peers".into()));
        });
    }

    {
        let ui_handle = app.as_weak();
        app.on_save_settings(move || {
            if let Some(ui) = ui_handle.upgrade() {
                let mut settings = DesktopSettings::load();
                let pipe_val = ui.get_daemon_pipe().to_string();
                settings.daemon_pipe = if pipe_val.is_empty() {
                    None
                } else {
                    Some(pipe_val)
                };
                settings.minimize_to_tray = ui.get_minimize_to_tray();
                let _ = settings.save();
            }
        });
    }

    // 6. Spawn Background DesktopEvent Dispatcher
    let ui_weak_clone = ui_weak.clone();
    rt.spawn(async move {
        while let Some(event) = event_rx.recv().await {
            let _ = ui_weak_clone.upgrade_in_event_loop(move |ui| {
                match event {
                    DesktopEvent::StatusChanged(status) => {
                        ui.set_agent_state(status.agent_state.into());
                        ui.set_peer_id(status.peer_id.into());
                        ui.set_version(status.version.into());
                        ui.set_is_operator_unlocked(status.is_operator_unlocked);
                    }
                    DesktopEvent::PeersChanged(peers) => {
                        let items: Vec<PeerItem> = peers
                            .into_iter()
                            .map(|p| PeerItem {
                                peer_id: p.peer_id.into(),
                                hostname: p.hostname.into(),
                                os: p.os.into(),
                                transport: p.transport.into(),
                                status: p.status.into(),
                            })
                            .collect();
                        ui.set_peers(slint::ModelRc::new(slint::VecModel::from(items)));
                    }
                    DesktopEvent::TicketsChanged(tickets) => {
                        let items: Vec<TicketItem> = tickets
                            .into_iter()
                            .map(|t| TicketItem {
                                id: t.id.into(),
                                title: t.title.into(),
                                priority: t.priority as i32,
                                state: t.state.into(),
                                created_at: format!("{}", t.created_at).into(),
                            })
                            .collect();
                        ui.set_tickets(slint::ModelRc::new(slint::VecModel::from(items)));
                    }
                    DesktopEvent::TicketLoaded(detail) => {
                        if let Some(d) = detail {
                            ui.set_selected_ticket_id(d.id.into());
                            ui.set_selected_ticket_title(d.title.into());
                            ui.set_selected_ticket_priority(d.priority as i32);
                            ui.set_selected_ticket_state(d.state.into());
                            ui.set_selected_ticket_epoch(
                                d.access_epoch.unwrap_or_default().into(),
                            );
                            let msgs: Vec<ChatMessageItem> = d
                                .messages
                                .into_iter()
                                .map(|m| ChatMessageItem {
                                    id: m.id.into(),
                                    sender: m.sender.into(),
                                    body: m.body.into(),
                                    is_operator: m.is_operator,
                                })
                                .collect();
                            ui.set_ticket_messages(slint::ModelRc::new(slint::VecModel::from(
                                msgs,
                            )));

                            let files: Vec<AttachmentItem> = d
                                .attachments
                                .into_iter()
                                .map(|f| AttachmentItem {
                                    id: f.id.into(),
                                    filename: f.filename.into(),
                                    size_str: format!("{} o", f.size_bytes).into(),
                                    sha256: f.sha256.into(),
                                })
                                .collect();
                            ui.set_ticket_files(slint::ModelRc::new(slint::VecModel::from(
                                files,
                            )));
                        } else {
                            ui.set_selected_ticket_id("".into());
                        }
                    }
                    DesktopEvent::ShellOpened { .. } => {
                        ui.set_terminal_connected(true);
                        ui.set_terminal_denied("".into());
                    }
                    DesktopEvent::ShellOutput(bytes) => {
                        let s = String::from_utf8_lossy(&bytes);
                        ui.set_terminal_text(s.to_string().into());
                    }
                    DesktopEvent::ShellClosed => {
                        ui.set_terminal_connected(false);
                    }
                    DesktopEvent::ShellDenied(msg) => {
                        ui.set_terminal_connected(false);
                        ui.set_terminal_denied(msg.into());
                    }
                    DesktopEvent::OperatorUnlocked(_) => {
                        ui.set_is_operator_unlocked(true);
                    }
                    DesktopEvent::OperatorLocked => {
                        ui.set_is_operator_unlocked(false);
                    }
                    DesktopEvent::SelfSupportChanged(status) => {
                        ui.set_db_healthy(!status.db_corrupt);
                        ui.set_peers_stale(status.peers_stale);
                        ui.set_last_action_result(
                            status.last_action_message.unwrap_or_default().into(),
                        );
                    }
                    DesktopEvent::Notification { message, .. } => {
                        ui.set_toast_message(message.into());
                    }
                    DesktopEvent::Error(err) => {
                        ui.set_toast_message(err.into());
                    }
                    _ => {}
                }
            });
        }
    });

    // 7. Run Slint GUI Event Loop
    app.run()?;

    info!("FORTIQ Desktop exited cleanly");
    Ok(())
}
