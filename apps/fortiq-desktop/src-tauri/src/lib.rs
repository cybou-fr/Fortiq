use serde::Serialize;
use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, WindowEvent,
};

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

        let pipe_name = fortiq_core::ipc::DEFAULT_WINDOWS_PIPE_NAME;
        let client = ClientOptions::new().open(pipe_name).ok()?;
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

fn show_main_window(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let open = MenuItem::with_id(app, "open", "Ouvrir FORTIQ", true, None::<&str>)?;
            let status = MenuItem::with_id(app, "status", "Statut : Prêt", false, None::<&str>)?;
            let separator = PredefinedMenuItem::separator(app)?;
            let quit = MenuItem::with_id(app, "quit", "Quitter FORTIQ", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&open, &status, &separator, &quit])?;

            let mut tray = TrayIconBuilder::with_id("fortiq")
                .tooltip("FORTIQ · Supervision P2P Souveraine")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "open" => show_main_window(app),
                    "quit" => app.exit(0),
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        show_main_window(tray.app_handle());
                    }
                });

            let icon = app
                .default_window_icon()
                .cloned()
                .unwrap_or_else(|| tauri::include_image!("icons/32x32.png"));
            tray = tray.icon(icon);
            let _ = tray.build(app);

            show_main_window(app.handle());
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            desktop_status,
            open_ticket,
            close_ticket,
            list_peers
        ])
        .run(tauri::generate_context!())
        .expect("error while running FORTIQ desktop");
}
