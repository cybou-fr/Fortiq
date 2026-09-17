use std::path::PathBuf;
use std::sync::Arc;
use tokio::sync::mpsc;

use fortiq_desktop::backend::BackendActor;
use fortiq_desktop::command::DesktopCommand;
use fortiq_desktop::event::DesktopEvent;
use fortiq_desktop::file_picker::{FilePicker, MockFilePicker};
use fortiq_desktop::instance_lock::InstanceLock;
use fortiq_desktop::ipc::IpcClient;
use fortiq_desktop::models::*;
use fortiq_desktop::settings::DesktopSettings;
use fortiq_desktop::terminal::keymap::{encode_key, KeyInput};
use fortiq_desktop::terminal::parser::TerminalPerformer;
use fortiq_desktop::terminal::screen::TerminalScreen;
use fortiq_desktop::terminal::session::TerminalSession;

#[test]
fn test_terminal_screen_and_parser() {
    let mut screen = TerminalScreen::new(40, 10);
    let mut parser = vte::Parser::new();

    let ansi_input = b"\x1b[31mRed \x1b[1;32mGreenBold\x1b[0m Plain\r\nLine2";
    {
        let mut performer = TerminalPerformer::new(&mut screen);
        parser.advance(&mut performer, ansi_input);
    }

    let text = screen.render_plain_text();
    let lines: Vec<&str> = text.lines().collect();
    assert!(lines.len() >= 2);
    assert_eq!(lines[0], "Red GreenBold Plain");
    assert_eq!(lines[1], "Line2");

    // Verify cell attributes
    let buf = screen.active_buffer();
    // 'R' in "Red" should be Red (0xFFE5484D) and not bold
    assert_eq!(buf[0][0].c, 'R');
    assert_eq!(buf[0][0].fg, 0xFFE5484D);
    assert!(!buf[0][0].bold);

    // 'G' in "GreenBold" should be Green (0xFF16A36D) and bold
    assert_eq!(buf[0][4].c, 'G');
    assert_eq!(buf[0][4].fg, 0xFF16A36D);
    assert!(buf[0][4].bold);

    // 'P' in "Plain" should be default reset fg (0xFFCCCCCC) and not bold
    assert_eq!(buf[0][14].c, 'P');
    assert_eq!(buf[0][14].fg, 0xFFCCCCCC);
    assert!(!buf[0][14].bold);
}

#[test]
fn test_terminal_cursor_movement_and_clearing() {
    let mut screen = TerminalScreen::new(20, 5);
    let mut parser = vte::Parser::new();

    // Position cursor at row 2, col 5 (1-based: row 3, col 6)
    let ansi_pos = b"\x1b[3;6HHello";
    {
        let mut performer = TerminalPerformer::new(&mut screen);
        parser.advance(&mut performer, ansi_pos);
    }

    assert_eq!(screen.cursor_row, 2);
    assert_eq!(screen.cursor_col, 10);
    let buf = screen.active_buffer();
    assert_eq!(buf[2][5].c, 'H');
    assert_eq!(buf[2][9].c, 'o');

    // Erase entire display
    {
        let mut performer = TerminalPerformer::new(&mut screen);
        parser.advance(&mut performer, b"\x1b[2J");
    }
    assert_eq!(screen.active_buffer()[2][5].c, ' ');
}

#[test]
fn test_terminal_keymap_encoding() {
    // Return key
    let ret = encode_key(&KeyInput {
        text: "\r".to_string(),
        ctrl: false,
        alt: false,
        shift: false,
    });
    assert_eq!(ret, Some(vec![b'\r']));

    // Backspace key
    let bksp = encode_key(&KeyInput {
        text: "\x08".to_string(),
        ctrl: false,
        alt: false,
        shift: false,
    });
    assert_eq!(bksp, Some(vec![0x7f]));

    // Ctrl+C
    let ctrl_c = encode_key(&KeyInput {
        text: "c".to_string(),
        ctrl: true,
        alt: false,
        shift: false,
    });
    assert_eq!(ctrl_c, Some(vec![0x03]));

    // Ctrl+D
    let ctrl_d = encode_key(&KeyInput {
        text: "d".to_string(),
        ctrl: true,
        alt: false,
        shift: false,
    });
    assert_eq!(ctrl_d, Some(vec![0x04]));

    // Arrow keys
    let up = encode_key(&KeyInput {
        text: "\u{F700}".to_string(),
        ctrl: false,
        alt: false,
        shift: false,
    });
    assert_eq!(up, Some(b"\x1b[A".to_vec()));
}

#[test]
fn test_instance_lock_acquisition() {
    let lock = InstanceLock::acquire();
    assert!(lock.is_ok());
    let lock = lock.unwrap();
    assert!(lock.path().exists());
}

#[test]
fn test_mock_file_picker() {
    let picker = MockFilePicker::default();
    picker
        .files_to_return
        .lock()
        .unwrap()
        .push(PathBuf::from("test.txt"));

    assert_eq!(picker.pick_file(), Some(PathBuf::from("test.txt")));
    assert_eq!(picker.pick_file(), None);
}

#[test]
fn test_settings_save_and_load() {
    let temp_dir = tempfile::tempdir().unwrap();
    let config_file = temp_dir.path().join("desktop.toml");

    let settings = DesktopSettings {
        refresh_interval_secs: 5,
        theme_mode: "dark".to_string(),
        daemon_pipe: Some(r"\\.\pipe\test-pipe".to_string()),
        minimize_to_tray: false,
        ..Default::default()
    };

    let serialized = toml::to_string_pretty(&settings).unwrap();
    std::fs::write(&config_file, serialized).unwrap();

    let content = std::fs::read_to_string(&config_file).unwrap();
    let loaded: DesktopSettings = toml::from_str(&content).unwrap();

    assert_eq!(loaded.refresh_interval_secs, 5);
    assert_eq!(loaded.theme_mode, "dark");
    assert_eq!(loaded.daemon_pipe, Some(r"\\.\pipe\test-pipe".to_string()));
    assert!(!loaded.minimize_to_tray);
}

#[tokio::test]
async fn test_terminal_session_ping_pong_and_resize() {
    use fortiq_shell::ShellFrame;
    use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

    let (client_io, server_io) = tokio::io::duplex(4096);
    let (cmd_tx, cmd_rx) = mpsc::channel(16);
    let (text_tx, mut text_rx) = mpsc::channel(16);

    // Mock terminal server
    let server_task = tokio::spawn(async move {
        let (server_read, mut server_write) = tokio::io::split(server_io);
        let mut reader = BufReader::new(server_read);
        let mut init_line = String::new();
        reader.read_line(&mut init_line).await.unwrap();
        // Handshake response
        server_write
            .write_all(b"{\"status\":\"ok\"}\n")
            .await
            .unwrap();
        server_write.flush().await.unwrap();

        // 1. Send Ping frame to client
        ShellFrame::Ping.write_to(&mut server_write).await.unwrap();

        // 2. Client reader loop must immediately reply with Pong!
        let pong_frame = ShellFrame::read_from(&mut reader).await.unwrap();
        assert_eq!(
            pong_frame,
            Some(ShellFrame::Pong),
            "Expected Pong frame in response to Ping"
        );

        // 3. Send remote data
        ShellFrame::Data(b"Terminal active".to_vec())
            .write_to(&mut server_write)
            .await
            .unwrap();
    });

    // Run client session
    let session_task = tokio::spawn(TerminalSession::run_with_stream(
        client_io,
        "peer-test".to_string(),
        Some("ticket-1".to_string()),
        80,
        24,
        cmd_rx,
        text_tx,
    ));

    // Verify text output arrived
    let mut got_output = false;
    let timeout = tokio::time::sleep(std::time::Duration::from_millis(800));
    tokio::pin!(timeout);

    loop {
        tokio::select! {
            Some(text) = text_rx.recv() => {
                if text.contains("Terminal active") {
                    got_output = true;
                    break;
                }
            }
            _ = &mut timeout => break,
        }
    }

    assert!(got_output, "Expected terminal text update from server data");
    let _ = cmd_tx
        .send(fortiq_desktop::terminal::TerminalCommand::Close)
        .await;
    server_task.abort();
    let _ = session_task.await;
}

#[tokio::test]
async fn test_backend_actor_operator_workflow() {
    let (cmd_tx, cmd_rx) = mpsc::channel::<DesktopCommand>(16);
    let (event_tx, mut event_rx) = mpsc::channel::<DesktopEvent>(16);

    let ipc = Arc::new(IpcClient::new(Some("mock-pipe".to_string()), None));
    let backend = BackendActor::new(ipc, cmd_rx, event_tx);

    let handle = tokio::spawn(backend.run());

    // 24-word valid test mnemonic
    let mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art";

    // Send UnlockOperator command (with offline pipe, must gracefully report IPC error)
    cmd_tx
        .send(DesktopCommand::UnlockOperator(mnemonic.to_string()))
        .await
        .unwrap();

    let mut got_response = false;
    let timeout = tokio::time::sleep(std::time::Duration::from_millis(500));
    tokio::pin!(timeout);

    loop {
        tokio::select! {
            Some(event) = event_rx.recv() => {
                match event {
                    DesktopEvent::OperatorUnlocked(_) | DesktopEvent::Error(_) => {
                        got_response = true;
                        break;
                    }
                    _ => {}
                }
            }
            _ = &mut timeout => break,
        }
    }

    assert!(
        got_response,
        "Expected response event from backend actor on unlock command"
    );

    // Send LockOperator command
    cmd_tx.send(DesktopCommand::LockOperator).await.unwrap();

    let mut locked = false;
    let timeout2 = tokio::time::sleep(std::time::Duration::from_millis(500));
    tokio::pin!(timeout2);

    loop {
        tokio::select! {
            Some(event) = event_rx.recv() => {
                if let DesktopEvent::OperatorLocked = event {
                    locked = true;
                    break;
                }
            }
            _ = &mut timeout2 => break,
        }
    }

    assert!(locked, "Expected OperatorLocked event from backend actor");
    handle.abort();
}

#[tokio::test]
async fn test_backend_actor_shell_rejects_local_loopback() {
    let (cmd_tx, cmd_rx) = mpsc::channel::<DesktopCommand>(16);
    let (event_tx, mut event_rx) = mpsc::channel::<DesktopEvent>(16);

    let ipc = Arc::new(IpcClient::new(Some("mock-pipe".to_string()), None));
    let backend = BackendActor::new(ipc, cmd_rx, event_tx);

    let handle = tokio::spawn(backend.run());

    // Send StartShell with unknown ticket or when ticket client matches local peer
    cmd_tx
        .send(DesktopCommand::StartShell {
            ticket_id: "ticket-local".to_string(),
            cols: 80,
            rows: 24,
        })
        .await
        .unwrap();

    let mut shell_denied = false;
    let timeout = tokio::time::sleep(std::time::Duration::from_millis(500));
    tokio::pin!(timeout);

    loop {
        tokio::select! {
            Some(event) = event_rx.recv() => {
                if let DesktopEvent::ShellDenied(msg) = event {
                    assert!(!msg.is_empty());
                    shell_denied = true;
                    break;
                }
            }
            _ = &mut timeout => break,
        }
    }

    assert!(
        shell_denied,
        "Expected ShellDenied event when target peer cannot resolve or loops back"
    );
    handle.abort();
}

#[test]
fn test_models_dto_serialization() {
    let status = DesktopStatusDto::default();
    let json = serde_json::to_string(&status).unwrap();
    let decoded: DesktopStatusDto = serde_json::from_str(&json).unwrap();
    assert_eq!(status, decoded);

    let ticket = TicketSummaryDto {
        id: "t-123".into(),
        title: "Test Ticket".into(),
        priority: 2,
        state: "OPEN".into(),
        created_at: 1000,
    };
    let json_ticket = serde_json::to_string(&ticket).unwrap();
    let decoded_ticket: TicketSummaryDto = serde_json::from_str(&json_ticket).unwrap();
    assert_eq!(ticket, decoded_ticket);
}
