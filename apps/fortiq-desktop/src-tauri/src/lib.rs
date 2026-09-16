use serde::Serialize;
use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, WindowEvent,
};

fn get_log_path() -> std::path::PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(std::path::PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| std::path::PathBuf::from(h).join(".fortiq")))
        .unwrap_or_else(|| std::path::PathBuf::from("."));
    base.join("FORTIQ").join("desktop.log")
}

pub fn log_diagnostic(msg: &str) {
    let now = std::time::SystemTime::now();
    let dur = now
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = dur.as_secs();
    let millis = dur.subsec_millis();
    let line = format!("[{secs}.{millis:03}] {msg}\n");
    print!("{line}");
    let path = get_log_path();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
    {
        use std::io::Write;
        let _ = f.write_all(line.as_bytes());
        let _ = f.flush();
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopStatus {
    pub product: String,
    pub version: String,
    pub agent_state: String,
    pub mode: String,
    pub peer_id: String,
    pub active_ticket_id: Option<String>,
    pub active_ticket_state: Option<String>,
    pub authorized_operator: Option<String>,
}

const MAX_IPC_LINE_BYTES: usize = 64 * 1024;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopPeer {
    pub peer_id: String,
    pub hostname: String,
    pub os: String,
    pub transport: String,
    pub status: String,
}

impl From<fortiq_core::ipc::PeerSummary> for DesktopPeer {
    fn from(p: fortiq_core::ipc::PeerSummary) -> Self {
        Self {
            peer_id: p.peer_id,
            hostname: p.hostname,
            os: p.os,
            transport: p.transport,
            status: p.status,
        }
    }
}

async fn send_ipc_request(
    req: &fortiq_core::ipc::IpcRequest,
) -> Option<fortiq_core::ipc::IpcResponse> {
    #[cfg(windows)]
    {
        use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
        use tokio::net::windows::named_pipe::ClientOptions;

        let pipe_name = fortiq_core::ipc::windows_pipe_name();
        let client = ClientOptions::new().open(&pipe_name).ok()?;
        let (read_half, mut write_half) = tokio::io::split(client);
        let mut reader = BufReader::new(read_half);

        let mut req_bytes = serde_json::to_vec(req).ok()?;
        req_bytes.push(b'\n');
        write_half.write_all(&req_bytes).await.ok()?;
        write_half.flush().await.ok()?;

        let mut line = String::new();
        let bytes_read = {
            let mut limiter = (&mut reader).take((MAX_IPC_LINE_BYTES + 1) as u64);
            limiter.read_line(&mut line).await.ok()?
        };
        if bytes_read > 0 && bytes_read <= MAX_IPC_LINE_BYTES {
            serde_json::from_str::<fortiq_core::ipc::IpcResponse>(line.trim()).ok()
        } else {
            None
        }
    }
    #[cfg(unix)]
    {
        use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
        use tokio::net::UnixStream;

        let path = std::env::var("FORTIQ_SOCK")
            .unwrap_or_else(|_| fortiq_core::ipc::DEFAULT_UNIX_SOCKET_PATH.to_owned());
        let stream = UnixStream::connect(&path).await.ok()?;
        let (read_half, mut write_half) = tokio::io::split(stream);
        let mut reader = BufReader::new(read_half);

        let mut req_bytes = serde_json::to_vec(req).ok()?;
        req_bytes.push(b'\n');
        write_half.write_all(&req_bytes).await.ok()?;
        write_half.flush().await.ok()?;

        let mut line = String::new();
        let bytes_read = {
            let mut limiter = (&mut reader).take((MAX_IPC_LINE_BYTES + 1) as u64);
            limiter.read_line(&mut line).await.ok()?
        };
        if bytes_read > 0 && bytes_read <= MAX_IPC_LINE_BYTES {
            serde_json::from_str::<fortiq_core::ipc::IpcResponse>(line.trim()).ok()
        } else {
            None
        }
    }
    #[cfg(not(any(windows, unix)))]
    {
        let _ = req;
        None
    }
}

#[tauri::command]
async fn desktop_status() -> DesktopStatus {
    log_diagnostic("[FRONTEND] desktop_status IPC invoked");
    if let Some(fortiq_core::ipc::IpcResponse::Status(status)) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::GetStatus).await
    {
        return DesktopStatus {
            product: status.product,
            version: status.version,
            agent_state: status.agent_state,
            mode: match status.mode {
                fortiq_core::NodeMode::Operator => "operator".to_string(),
                fortiq_core::NodeMode::Managed => "managed".to_string(),
            },
            peer_id: status.peer_id,
            active_ticket_id: status.active_ticket.as_ref().map(|t| t.id.to_string()),
            active_ticket_state: status
                .active_ticket
                .as_ref()
                .map(|t| format!("{:?}", t.state).to_uppercase()),
            authorized_operator: status.authorized_operator,
        };
    }

    // Explicit offline state when daemon is not reachable
    DesktopStatus {
        product: "FORTIQ".to_string(),
        version: env!("CARGO_PKG_VERSION").to_string(),
        agent_state: "offline".to_string(),
        mode: "offline".to_string(),
        peer_id: String::new(),
        active_ticket_id: None,
        active_ticket_state: None,
        authorized_operator: None,
    }
}

#[tauri::command]
async fn open_ticket() -> Result<String, String> {
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::OpenTicket).await {
        match resp {
            fortiq_core::ipc::IpcResponse::TicketOpened(t) => Ok(t.id.to_string()),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible (démon hors-ligne)".to_string())
    }
}

#[tauri::command]
async fn open_remote_ticket(peer: String) -> Result<(), String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::OpenRemoteTicket { peer }).await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::RemoteTicketOpened => Ok(()),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible (démon hors-ligne)".to_string())
    }
}

#[tauri::command]
async fn close_ticket(peer: String) -> Result<(), String> {
    if let Some(resp) =
        send_ipc_request(&fortiq_core::ipc::IpcRequest::CloseTicket { peer, dial: None }).await
    {
        match resp {
            fortiq_core::ipc::IpcResponse::TicketClosed => Ok(()),
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible (démon hors-ligne)".to_string())
    }
}

#[tauri::command]
async fn list_peers() -> Result<Vec<DesktopPeer>, String> {
    if let Some(resp) = send_ipc_request(&fortiq_core::ipc::IpcRequest::ListPeers).await {
        match resp {
            fortiq_core::ipc::IpcResponse::Peers(peers) => {
                Ok(peers.into_iter().map(DesktopPeer::from).collect())
            }
            fortiq_core::ipc::IpcResponse::Error(err) => Err(err),
            _ => Err("Réponse inattendue du démon".to_string()),
        }
    } else {
        Err("Service FORTIQ indisponible (démon hors-ligne)".to_string())
    }
}

pub struct TerminalState(
    pub tokio::sync::Mutex<Option<tokio::sync::mpsc::Sender<fortiq_shell::ShellFrame>>>,
);

#[tauri::command]
async fn start_terminal_session(
    app: tauri::AppHandle,
    state: tauri::State<'_, TerminalState>,
    peer: String,
    cols: u16,
    rows: u16,
) -> Result<(), String> {
    use tauri::Emitter;
    use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

    #[cfg(windows)]
    let stream = {
        use tokio::net::windows::named_pipe::ClientOptions;
        let pipe_name = fortiq_core::ipc::windows_terminal_pipe_name();
        ClientOptions::new().open(&pipe_name).map_err(|e| {
            format!("Impossible de se connecter au pipe terminal ({pipe_name}): {e}")
        })?
    };

    #[cfg(unix)]
    let stream = {
        use tokio::net::UnixStream;
        let path = fortiq_core::ipc::unix_terminal_socket_path();
        UnixStream::connect(&path)
            .await
            .map_err(|e| format!("Impossible de se connecter au socket terminal ({path}): {e}"))?
    };

    #[cfg(not(any(windows, unix)))]
    return Err("Plateforme non supportée".to_string());

    let (read_half, mut write_half) = tokio::io::split(stream);

    let init = fortiq_core::ipc::TerminalSessionInit {
        peer,
        cols,
        rows,
        dial: None,
    };
    let mut init_bytes = serde_json::to_vec(&init).map_err(|e| e.to_string())?;
    init_bytes.push(b'\n');
    write_half
        .write_all(&init_bytes)
        .await
        .map_err(|e| e.to_string())?;
    write_half.flush().await.map_err(|e| e.to_string())?;

    let mut reader = BufReader::new(read_half);
    let mut status_line = String::new();
    reader
        .read_line(&mut status_line)
        .await
        .map_err(|e| format!("Échec de lecture du handshake: {e}"))?;

    #[derive(serde::Deserialize)]
    struct HandshakeResp {
        status: String,
        message: Option<String>,
    }

    let resp: HandshakeResp = serde_json::from_str(status_line.trim())
        .map_err(|e| format!("Réponse handshake invalide: {e}"))?;

    if resp.status != "ok" {
        return Err(resp
            .message
            .unwrap_or_else(|| "Connexion terminal refusée par le démon".to_string()));
    }

    let (tx, mut rx) = tokio::sync::mpsc::channel::<fortiq_shell::ShellFrame>(128);

    {
        let mut session_guard = state.0.lock().await;
        *session_guard = Some(tx);
    }

    tokio::spawn(async move {
        while let Some(frame) = rx.recv().await {
            if frame.write_to(&mut write_half).await.is_err() {
                break;
            }
        }
    });

    let mut read_half = reader.into_inner();
    let app_clone = app.clone();
    tokio::spawn(async move {
        loop {
            match fortiq_shell::ShellFrame::read_from(&mut read_half).await {
                Ok(Some(fortiq_shell::ShellFrame::Data(bytes))) => {
                    let text = String::from_utf8_lossy(&bytes).to_string();
                    let _ = app_clone.emit("terminal-output", text);
                }
                Ok(Some(fortiq_shell::ShellFrame::Ping)) => {}
                Ok(Some(fortiq_shell::ShellFrame::Pong)) => {}
                Ok(Some(fortiq_shell::ShellFrame::Resize { .. })) => {}
                Ok(None) => break,
                Err(_) => break,
            }
        }
        let _ = app_clone.emit("terminal-closed", ());
    });

    Ok(())
}

#[tauri::command]
async fn write_terminal_data(
    state: tauri::State<'_, TerminalState>,
    data: String,
) -> Result<(), String> {
    let guard = state.0.lock().await;
    if let Some(tx) = guard.as_ref() {
        tx.send(fortiq_shell::ShellFrame::Data(data.into_bytes()))
            .await
            .map_err(|e| format!("Échec d'envoi des données terminal: {e}"))?;
        Ok(())
    } else {
        Err("Aucune session terminal active".to_string())
    }
}

#[tauri::command]
async fn resize_terminal(
    state: tauri::State<'_, TerminalState>,
    cols: u16,
    rows: u16,
) -> Result<(), String> {
    let guard = state.0.lock().await;
    if let Some(tx) = guard.as_ref() {
        tx.send(fortiq_shell::ShellFrame::Resize { cols, rows })
            .await
            .map_err(|e| format!("Échec d'envoi du redimensionnement: {e}"))?;
        Ok(())
    } else {
        Ok(())
    }
}

#[tauri::command]
async fn close_terminal_session(state: tauri::State<'_, TerminalState>) -> Result<(), String> {
    let mut guard = state.0.lock().await;
    *guard = None;
    Ok(())
}

fn show_main_window(app: &tauri::AppHandle) {
    log_diagnostic("[ACTION] show_main_window triggered");
    if let Some(window) = app.get_webview_window("main") {
        match window.show() {
            Ok(_) => log_diagnostic("[ACTION] window.show() success"),
            Err(e) => log_diagnostic(&format!("[ACTION ERROR] window.show() failed: {e}")),
        }
        let _ = window.unminimize();
        let _ = window.set_focus();
        log_diagnostic("[ACTION] window unminimized and focused");
    } else {
        log_diagnostic("[ACTION ERROR] 'main' window not found!");
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    log_diagnostic("=== FORTIQ Desktop starting ===");
    log_diagnostic(&format!("Log file path: {}", get_log_path().display()));

    tauri::Builder::default()
        .manage(TerminalState(tokio::sync::Mutex::new(None)))
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            log_diagnostic("[SETUP] Beginning application setup...");

            let open = MenuItem::with_id(app, "open", "Ouvrir FORTIQ", true, None::<&str>)?;
            let status = MenuItem::with_id(app, "status", "Statut : Prêt", false, None::<&str>)?;
            let separator = PredefinedMenuItem::separator(app)?;
            let quit = MenuItem::with_id(app, "quit", "Quitter FORTIQ", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&open, &status, &separator, &quit])?;
            log_diagnostic("[SETUP] Tray menu created");

            let mut tray = TrayIconBuilder::with_id("fortiq")
                .tooltip("FORTIQ · Supervision P2P Souveraine")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "open" => {
                        log_diagnostic("[TRAY] Menu event 'open' clicked");
                        show_main_window(app);
                    }
                    "quit" => {
                        log_diagnostic("[TRAY] Menu event 'quit' clicked, exiting app");
                        app.exit(0);
                    }
                    other => {
                        log_diagnostic(&format!("[TRAY] Menu event '{other}' clicked"));
                    }
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        log_diagnostic("[TRAY] Tray icon Left-Click detected, showing window");
                        show_main_window(tray.app_handle());
                    }
                });

            let icon = match app.default_window_icon() {
                Some(icon) => {
                    log_diagnostic("[SETUP] Using default_window_icon for tray");
                    icon.clone()
                }
                None => {
                    log_diagnostic(
                        "[SETUP] default_window_icon is None, using embedded 32x32 icon",
                    );
                    tauri::include_image!("icons/32x32.png")
                }
            };
            tray = tray.icon(icon);

            log_diagnostic("[SETUP] Building tray icon...");
            match tray.build(app) {
                Ok(_) => {
                    log_diagnostic("[SETUP] Tray icon registered successfully in Windows tray!");
                }
                Err(e) => {
                    log_diagnostic(&format!("[SETUP ERROR] Failed to build tray icon: {e}"));
                    eprintln!("FATAL: Failed to build tray icon: {e}");
                    return Err(Box::new(e));
                }
            }

            log_diagnostic("[SETUP] Querying 'main' WebviewWindow...");
            match app.get_webview_window("main") {
                Some(window) => {
                    log_diagnostic("[SETUP] 'main' window found in app registry");
                    match window.show() {
                        Ok(_) => log_diagnostic("[SETUP] Initial window.show() succeeded"),
                        Err(e) => {
                            log_diagnostic(&format!("[SETUP ERROR] window.show() failed: {e}"))
                        }
                    }
                    let _ = window.unminimize();
                    let _ = window.set_focus();
                }
                None => {
                    log_diagnostic("[SETUP ERROR] 'main' window NOT FOUND in app registry!");
                }
            }

            log_diagnostic("[SETUP] Setup completed successfully");
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                log_diagnostic(
                    "[WINDOW] CloseRequested received -> preventing close, hiding to tray",
                );
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            desktop_status,
            open_ticket,
            open_remote_ticket,
            close_ticket,
            list_peers,
            start_terminal_session,
            write_terminal_data,
            resize_terminal,
            close_terminal_session
        ])
        .run(tauri::generate_context!())
        .expect("error while running FORTIQ desktop");
}
