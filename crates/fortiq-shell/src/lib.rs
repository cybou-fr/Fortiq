#![allow(deprecated)]

use std::path::Path;
use std::sync::{Arc, Mutex};

use anyhow::{Context, Result};
use fortiq_core::NodeInfo;
use futures::{
    AsyncRead, AsyncReadExt as FuturesAsyncReadExt, AsyncWrite,
    AsyncWriteExt as FuturesAsyncWriteExt,
};
use tokio::io::AsyncWriteExt;
use tokio_util::compat::FuturesAsyncReadCompatExt;

pub const AUTHORIZED: u8 = 1;
pub const DENIED: u8 = 0;
/// The remote peer has no open ticket, so its user has not consented.
pub const DENIED_NO_TICKET: u8 = 2;
/// Another shell session is already running on the remote peer.
pub const DENIED_BUSY: u8 = 3;
/// The ticket for this session is closed.
pub const DENIED_TICKET_CLOSED: u8 = 4;
/// Remote access for this ticket is currently disabled by the client.
pub const DENIED_REMOTE_ACCESS_DISABLED: u8 = 5;

/// How often the served session pings an idle operator.
const KEEPALIVE_INTERVAL: std::time::Duration = std::time::Duration::from_secs(15);
/// How long the served session waits for any frame before giving up on the
/// operator. An operator that vanishes without closing the stream (a killed
/// console, a dropped link) used to leave `serve` blocked forever, which held
/// the single-session flag and made every later session impossible.
const LIVENESS_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(60);

/// Explains a refused shell so the operator learns which gate closed instead of
/// reading one ambiguous sentence.
pub fn describe_denial(code: u8) -> &'static str {
    match code {
        DENIED_NO_TICKET => "no open ticket on the remote host: its user has not opened one",
        DENIED_BUSY => "another shell session is already active on the remote host",
        DENIED_TICKET_CLOSED => "the ticket for this session is closed",
        DENIED_REMOTE_ACCESS_DISABLED => {
            "remote access for this ticket is currently disabled by the client"
        }
        _ => "the remote host refused the terminal",
    }
}

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct ShellHandshake {
    pub ticket_id: String,
}

impl ShellHandshake {
    pub fn new(ticket_id: String) -> Self {
        Self { ticket_id }
    }

    pub async fn write_to_async<W: futures::AsyncWrite + Unpin>(
        &self,
        writer: &mut W,
    ) -> Result<()> {
        use futures::AsyncWriteExt;
        let json = serde_json::to_vec(self)?;
        let len = u16::try_from(json.len()).map_err(|_| anyhow::anyhow!("handshake too large"))?;
        writer.write_all(&len.to_be_bytes()).await?;
        writer.write_all(&json).await?;
        writer.flush().await?;
        Ok(())
    }

    pub async fn read_from_async<R: futures::AsyncRead + Unpin>(reader: &mut R) -> Result<Self> {
        use futures::AsyncReadExt;
        let mut len_bytes = [0u8; 2];
        reader.read_exact(&mut len_bytes).await?;
        let len = u16::from_be_bytes(len_bytes) as usize;
        let mut buf = vec![0u8; len];
        reader.read_exact(&mut buf).await?;
        let handshake = serde_json::from_slice(&buf)?;
        Ok(handshake)
    }

    pub async fn write_to_tokio<W: tokio::io::AsyncWrite + Unpin>(
        &self,
        writer: &mut W,
    ) -> Result<()> {
        use tokio::io::AsyncWriteExt;
        let json = serde_json::to_vec(self)?;
        let len = u16::try_from(json.len()).map_err(|_| anyhow::anyhow!("handshake too large"))?;
        writer.write_all(&len.to_be_bytes()).await?;
        writer.write_all(&json).await?;
        writer.flush().await?;
        Ok(())
    }

    pub async fn read_from_tokio<R: tokio::io::AsyncRead + Unpin>(reader: &mut R) -> Result<Self> {
        use tokio::io::AsyncReadExt;
        let mut len_bytes = [0u8; 2];
        reader.read_exact(&mut len_bytes).await?;
        let len = u16::from_be_bytes(len_bytes) as usize;
        let mut buf = vec![0u8; len];
        reader.read_exact(&mut buf).await?;
        let handshake = serde_json::from_slice(&buf)?;
        Ok(handshake)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ShellFrame {
    Data(Vec<u8>),
    Resize { cols: u16, rows: u16 },
    Ping,
    Pong,
}

impl ShellFrame {
    pub const TAG_DATA: u8 = 0x00;
    pub const TAG_RESIZE: u8 = 0x01;
    pub const TAG_PING: u8 = 0x02;
    pub const TAG_PONG: u8 = 0x03;

    pub async fn read_from<R: tokio::io::AsyncRead + Unpin>(
        reader: &mut R,
    ) -> std::io::Result<Option<Self>> {
        use tokio::io::AsyncReadExt;
        let mut header = [0u8; 3];
        match reader.read_exact(&mut header).await {
            Ok(_) => {}
            Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
            Err(e) => return Err(e),
        }

        let tag = header[0];
        let len = u16::from_be_bytes([header[1], header[2]]) as usize;

        match tag {
            Self::TAG_DATA => {
                let mut buf = vec![0u8; len];
                reader.read_exact(&mut buf).await?;
                Ok(Some(Self::Data(buf)))
            }
            Self::TAG_RESIZE => {
                if len != 4 {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::InvalidData,
                        "invalid resize frame length",
                    ));
                }
                let mut dims = [0u8; 4];
                reader.read_exact(&mut dims).await?;
                let cols = u16::from_be_bytes([dims[0], dims[1]]);
                let rows = u16::from_be_bytes([dims[2], dims[3]]);
                Ok(Some(Self::Resize { cols, rows }))
            }
            Self::TAG_PING => Ok(Some(Self::Ping)),
            Self::TAG_PONG => Ok(Some(Self::Pong)),
            _ => Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                format!("unknown shell frame tag: {tag}"),
            )),
        }
    }

    pub async fn write_to<W: tokio::io::AsyncWrite + Unpin>(
        &self,
        writer: &mut W,
    ) -> std::io::Result<()> {
        match self {
            Self::Data(bytes) => {
                let len = u16::try_from(bytes.len()).map_err(|_| {
                    std::io::Error::new(
                        std::io::ErrorKind::InvalidInput,
                        "shell frame data payload exceeds 65535 bytes",
                    )
                })?;
                let mut header = [Self::TAG_DATA, 0, 0];
                let len_bytes = len.to_be_bytes();
                header[1] = len_bytes[0];
                header[2] = len_bytes[1];
                writer.write_all(&header).await?;
                writer.write_all(bytes).await?;
                writer.flush().await?;
            }
            Self::Resize { cols, rows } => {
                let mut packet = [Self::TAG_RESIZE, 0, 4, 0, 0, 0, 0];
                let c = cols.to_be_bytes();
                let r = rows.to_be_bytes();
                packet[3] = c[0];
                packet[4] = c[1];
                packet[5] = r[0];
                packet[6] = r[1];
                writer.write_all(&packet).await?;
                writer.flush().await?;
            }
            Self::Ping => {
                writer.write_all(&[Self::TAG_PING, 0, 0]).await?;
                writer.flush().await?;
            }
            Self::Pong => {
                writer.write_all(&[Self::TAG_PONG, 0, 0]).await?;
                writer.flush().await?;
            }
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShellKind {
    Pwsh,
    PowerShell,
    Cmd,
    Bash,
    Sh,
}

impl ShellKind {
    pub fn name(self) -> &'static str {
        match self {
            Self::Pwsh => "PowerShell (pwsh)",
            Self::PowerShell => "Windows PowerShell",
            Self::Cmd => "cmd",
            Self::Bash => "bash",
            Self::Sh => "sh",
        }
    }

    #[cfg(target_os = "linux")]
    fn path(self) -> &'static str {
        match self {
            Self::Bash => "/bin/bash",
            Self::Sh => "/bin/sh",
            Self::Pwsh | Self::PowerShell | Self::Cmd => {
                unreachable!("Windows shell selected on Linux")
            }
        }
    }

    #[cfg(target_os = "windows")]
    fn program(self) -> &'static str {
        match self {
            Self::Pwsh => "pwsh.exe",
            Self::PowerShell => "powershell.exe",
            Self::Cmd => "cmd.exe",
            Self::Bash | Self::Sh => unreachable!("Linux shell selected on Windows"),
        }
    }

    #[cfg(target_os = "windows")]
    fn arguments(self) -> &'static [&'static str] {
        match self {
            Self::Pwsh | Self::PowerShell => &["-NoLogo", "-NoProfile"],
            Self::Cmd => &["/Q"],
            Self::Bash | Self::Sh => unreachable!("Linux shell selected on Windows"),
        }
    }
}

pub fn detect_linux_shell() -> Result<ShellKind> {
    if Path::new("/bin/bash").is_file() {
        return Ok(ShellKind::Bash);
    }
    if Path::new("/bin/sh").is_file() {
        return Ok(ShellKind::Sh);
    }
    anyhow::bail!("neither /bin/bash nor /bin/sh is available")
}

#[cfg(target_os = "windows")]
pub fn detect_windows_shell() -> Result<ShellKind> {
    detect_windows_shell_with(executable_in_path)
        .context("none of pwsh.exe, powershell.exe, or cmd.exe is available")
}

#[cfg(target_os = "windows")]
fn detect_windows_shell_with(mut exists: impl FnMut(&str) -> bool) -> Option<ShellKind> {
    [
        ("pwsh.exe", ShellKind::Pwsh),
        ("powershell.exe", ShellKind::PowerShell),
        ("cmd.exe", ShellKind::Cmd),
    ]
    .into_iter()
    .find_map(|(program, kind)| exists(program).then_some(kind))
}

#[cfg(target_os = "windows")]
fn executable_in_path(program: &str) -> bool {
    resolve_executable_in_path(program).is_some()
}

#[cfg(target_os = "windows")]
fn resolve_executable_in_path(program: &str) -> Option<std::path::PathBuf> {
    std::env::var_os("PATH").and_then(|path| {
        std::env::split_paths(&path)
            .map(|directory| directory.join(program))
            .find(|candidate| candidate.is_file())
    })
}

pub async fn send_authorization<S>(stream: &mut S, allowed: bool) -> Result<()>
where
    S: AsyncWrite + Unpin,
{
    send_authorization_code(stream, if allowed { AUTHORIZED } else { DENIED }).await
}

pub async fn send_authorization_code<S>(stream: &mut S, code: u8) -> Result<()>
where
    S: AsyncWrite + Unpin,
{
    stream.write_all(&[code]).await?;
    stream.flush().await?;
    Ok(())
}

pub async fn serve<S>(stream: S, local_info: NodeInfo) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    serve_with_revocation(
        stream,
        local_info,
        tokio_util::sync::CancellationToken::new(),
    )
    .await
}

pub async fn serve_with_revocation<S>(
    stream: S,
    local_info: NodeInfo,
    revocation_token: tokio_util::sync::CancellationToken,
) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let (network_read, network_write) = tokio::io::split(stream.compat());
    serve_pty(network_read, network_write, local_info, revocation_token).await
}

async fn serve_pty<R, W>(
    mut network_read: R,
    mut network_write: W,
    local_info: NodeInfo,
    revocation_token: tokio_util::sync::CancellationToken,
) -> Result<()>
where
    R: tokio::io::AsyncRead + Unpin + Send + 'static,
    W: tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let pty_system = portable_pty::native_pty_system();
    let pair = pty_system
        .openpty(portable_pty::PtySize {
            rows: 24,
            cols: 80,
            pixel_width: 0,
            pixel_height: 0,
        })
        .context("failed to open pseudo-terminal (PTY)")?;

    #[cfg(target_os = "windows")]
    let (shell, cmd) = {
        let shell = detect_windows_shell()?;
        // ConPTY/CreateProcess does not reliably search PATH when invoked from
        // a background service. Pass the exact executable already discovered
        // by shell selection instead of only `pwsh.exe`/`powershell.exe`.
        let program = resolve_executable_in_path(shell.program())
            .unwrap_or_else(|| std::path::PathBuf::from(shell.program()));
        let mut cmd = portable_pty::CommandBuilder::new(program);
        for arg in shell.arguments() {
            cmd.arg(arg);
        }
        (shell, cmd)
    };

    #[cfg(target_os = "linux")]
    let (shell, cmd) = {
        let shell = detect_linux_shell()?;
        let mut cmd = portable_pty::CommandBuilder::new(shell.path());
        // systemd services commonly start without HOME. Interactive shells then
        // expand "$HOME/.cargo/env" as "/.cargo/env" and other profile scripts
        // also behave incorrectly. FORTIQ's Linux package runs as root today,
        // so use root's real home only when the inherited value is absent.
        if std::env::var_os("HOME").is_none() && Path::new("/root").is_dir() {
            cmd.env("HOME", "/root");
        }
        (shell, cmd)
    };

    #[cfg(not(any(target_os = "linux", target_os = "windows")))]
    anyhow::bail!("remote shell serving is not implemented on this operating system");

    let mut child = pair
        .slave
        .spawn_command(cmd)
        .with_context(|| format!("failed to spawn shell {}", shell.name()))?;

    let master_reader = pair
        .master
        .try_clone_reader()
        .context("failed to clone PTY reader")?;
    let mut master_writer = pair
        .master
        .take_writer()
        .context("failed to take PTY writer")?;

    let master = Arc::new(Mutex::new(Some(pair.master)));

    // Send styled terminal banner
    let banner = format!(
        "\r\n\x1b[1;36mFORTIQ Remote Session\x1b[0m\r\n\r\nHost: {}\r\nPeerId: {}\r\nOS: {}\r\nArch: {}\r\nShell: {}\r\nFORTIQ: {}\r\n\r\n",
        local_info.name,
        local_info.peer_id,
        local_info.os,
        local_info.arch,
        shell.name(),
        local_info.version
    );
    ShellFrame::Data(banner.into_bytes())
        .write_to(&mut network_write)
        .await?;

    // Task 3: write bytes to PTY master
    let (pty_in_tx, mut pty_in_rx) = tokio::sync::mpsc::channel::<Vec<u8>>(128);
    let pty_in_tx_for_dsr = pty_in_tx.clone();
    let mut write_task = tokio::task::spawn_blocking(move || {
        use std::io::Write;
        while let Some(bytes) = pty_in_rx.blocking_recv() {
            if master_writer.write_all(&bytes).is_err() || master_writer.flush().is_err() {
                break;
            }
        }
    });

    // Task 1: read bytes from PTY master and queue them for network transmission.
    // Keepalive pings and pongs share this channel so a single task owns the
    // network writer.
    let (pty_out_tx, mut pty_out_rx) = tokio::sync::mpsc::channel::<ShellFrame>(128);
    let keepalive_tx = pty_out_tx.clone();
    let mut read_task = tokio::task::spawn_blocking(move || {
        use std::io::Read;
        let mut reader = master_reader;
        let mut buf = [0u8; 4096];
        loop {
            match reader.read(&mut buf) {
                Ok(0) => break,
                Ok(n) => {
                    // If the shell requests cursor position (DSR \x1b[6n, e.g. PowerShell PSReadLine),
                    // reply with \x1b[1;1R so the shell doesn't block waiting for a terminal emulator.
                    if buf[..n].windows(4).any(|w| w == b"\x1b[6n") {
                        let _ = pty_in_tx_for_dsr.blocking_send(b"\x1b[1;1R".to_vec());
                    }
                    if pty_out_tx
                        .blocking_send(ShellFrame::Data(buf[..n].to_vec()))
                        .is_err()
                    {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });

    // Task 2: forward pty_out_rx to network_write
    let mut send_task = tokio::spawn(async move {
        while let Some(frame) = pty_out_rx.recv().await {
            if frame.write_to(&mut network_write).await.is_err() {
                break;
            }
        }
    });

    // Task 4: ping an idle operator so a session that is merely quiet is not
    // mistaken for an abandoned one.
    let ping_tx = keepalive_tx.clone();
    let keepalive_task = tokio::spawn(async move {
        let mut ticker = tokio::time::interval(KEEPALIVE_INTERVAL);
        ticker.tick().await;
        loop {
            ticker.tick().await;
            if ping_tx.send(ShellFrame::Ping).await.is_err() {
                break;
            }
        }
    });

    let mut killer = child.clone_killer();
    let mut child_task = tokio::task::spawn_blocking(move || child.wait());

    let master_for_resize = Arc::clone(&master);
    let mut child_completed = false;
    let mut send_completed = false;

    tokio::select! {
        _ = revocation_token.cancelled() => {
            tracing::warn!("Remote shell session cancelled immediately by client revocation");
        }
        res = &mut child_task => {
            child_completed = true;
            tracing::debug!("Child shell process exited: {:?}", res);
        }
        _ = &mut send_task => {
            send_completed = true;
        }
        _ = async {
            loop {
                // Any frame proves the operator is still there. Without this
                // deadline an abandoned stream kept the session, and therefore
                // the single-session flag, alive forever.
                let frame = match tokio::time::timeout(
                    LIVENESS_TIMEOUT,
                    ShellFrame::read_from(&mut network_read),
                )
                .await
                {
                    Ok(result) => result,
                    Err(_) => {
                        tracing::warn!(
                            "no frame from the operator within the liveness timeout; ending session"
                        );
                        break;
                    }
                };
                match frame {
                    Ok(Some(ShellFrame::Data(bytes))) => {
                        if pty_in_tx.send(bytes).await.is_err() {
                            break;
                        }
                    }
                    Ok(Some(ShellFrame::Resize { cols, rows })) => {
                        let m = Arc::clone(&master_for_resize);
                        let _ = tokio::task::spawn_blocking(move || {
                            if let Some(master) = m.lock().unwrap().as_ref() {
                                let _ = master.resize(portable_pty::PtySize {
                                    rows,
                                    cols,
                                    pixel_width: 0,
                                    pixel_height: 0,
                                });
                            }
                        })
                        .await;
                    }
                    Ok(Some(ShellFrame::Ping)) => {
                        let _ = keepalive_tx.send(ShellFrame::Pong).await;
                    }
                    Ok(Some(ShellFrame::Pong)) => {}
                    Ok(None) => break, // EOF
                    Err(_) => break,
                }
            }
        } => {}
    }

    // Give a brief window for remaining buffered output to drain
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;

    keepalive_task.abort();
    let _ = killer.kill();
    drop(pty_in_tx);
    let _ = tokio::time::timeout(std::time::Duration::from_millis(300), &mut write_task).await;

    // Drop the master PTY handle so ConPTY closes the pseudo console and unblocks master_reader
    master.lock().unwrap().take();

    let _ = tokio::time::timeout(std::time::Duration::from_millis(300), &mut read_task).await;
    read_task.abort();

    if !send_completed {
        let _ = tokio::time::timeout(std::time::Duration::from_millis(300), &mut send_task).await;
        send_task.abort();
    }

    if !child_completed {
        let _ = tokio::time::timeout(std::time::Duration::from_millis(300), &mut child_task).await;
    }

    Ok(())
}

pub async fn run_client<S>(mut stream: S, command: Option<String>) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let mut authorization = [0_u8; 1];
    stream
        .read_exact(&mut authorization)
        .await
        .context("remote peer closed before shell authorization")?;
    if authorization[0] != AUTHORIZED {
        anyhow::bail!("remote peer denied shell authorization");
    }

    let (mut remote_read, mut remote_write) = tokio::io::split(stream.compat());

    if let Some(command) = command {
        let cmd_bytes = format!("{command}\r\nexit\r\n").into_bytes();
        ShellFrame::Data(cmd_bytes)
            .write_to(&mut remote_write)
            .await?;
        while let Ok(Some(frame)) = ShellFrame::read_from(&mut remote_read).await {
            match frame {
                ShellFrame::Data(bytes) => {
                    tokio::io::stdout().write_all(&bytes).await?;
                    tokio::io::stdout().flush().await?;
                }
                ShellFrame::Ping => {
                    let _ = ShellFrame::Pong.write_to(&mut remote_write).await;
                }
                _ => {}
            }
        }
        return Ok(());
    }

    let send_task = tokio::spawn(async move {
        use tokio::io::AsyncReadExt;
        let mut stdin = tokio::io::stdin();
        let mut buf = [0u8; 1024];
        loop {
            match stdin.read(&mut buf).await {
                Ok(0) => break,
                Ok(n) => {
                    if ShellFrame::Data(buf[..n].to_vec())
                        .write_to(&mut remote_write)
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

    while let Ok(Some(frame)) = ShellFrame::read_from(&mut remote_read).await {
        if let ShellFrame::Data(bytes) = frame {
            tokio::io::stdout().write_all(&bytes).await?;
            tokio::io::stdout().flush().await?;
        }
    }

    send_task.abort();
    let _ = send_task.await;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(target_os = "linux")]
    #[test]
    fn detects_an_available_linux_shell() {
        assert!(matches!(
            detect_linux_shell().unwrap(),
            ShellKind::Bash | ShellKind::Sh
        ));
    }

    #[cfg(target_os = "windows")]
    #[test]
    fn windows_shell_selection_uses_required_priority() {
        assert_eq!(detect_windows_shell_with(|_| true), Some(ShellKind::Pwsh));
        assert_eq!(
            detect_windows_shell_with(|program| program != "pwsh.exe"),
            Some(ShellKind::PowerShell)
        );
        assert_eq!(
            detect_windows_shell_with(|program| program == "cmd.exe"),
            Some(ShellKind::Cmd)
        );
        assert_eq!(detect_windows_shell_with(|_| false), None);
    }

    #[cfg(target_os = "windows")]
    #[test]
    fn detects_an_available_windows_shell() {
        assert!(matches!(
            detect_windows_shell().unwrap(),
            ShellKind::Pwsh | ShellKind::PowerShell | ShellKind::Cmd
        ));
    }

    #[tokio::test]
    async fn frame_encoding_decoding_roundtrip() {
        let (mut client, mut server) = tokio::io::duplex(1024);

        let data_frame = ShellFrame::Data(b"hello conpty".to_vec());
        data_frame.write_to(&mut client).await.unwrap();

        let received = ShellFrame::read_from(&mut server).await.unwrap().unwrap();
        assert_eq!(received, ShellFrame::Data(b"hello conpty".to_vec()));

        let resize_frame = ShellFrame::Resize {
            cols: 120,
            rows: 40,
        };
        resize_frame.write_to(&mut client).await.unwrap();

        let received_resize = ShellFrame::read_from(&mut server).await.unwrap().unwrap();
        assert_eq!(
            received_resize,
            ShellFrame::Resize {
                cols: 120,
                rows: 40
            }
        );
    }

    #[tokio::test]
    async fn test_serve_and_run_client_command() {
        use tokio_util::compat::TokioAsyncReadCompatExt;
        let (client, server) = tokio::io::duplex(4096);
        let client = client.compat();
        let mut server = server.compat();

        let node_info = NodeInfo::local(
            "12D3KooWD3XWsmNtAiqrmFmC6D5gY8QZk4T5D2xSm9R9aZg7kF8h"
                .parse()
                .unwrap(),
            "test-node".to_string(),
            fortiq_core::NodeMode::Managed,
        );

        let server_task = tokio::spawn(async move {
            send_authorization(&mut server, true).await.unwrap();
            serve(server, node_info).await
        });

        let client_res = tokio::time::timeout(
            std::time::Duration::from_secs(20),
            run_client(client, Some("whoami".to_string())),
        )
        .await;

        println!("client_res: {:?}", client_res);
        assert!(client_res.is_ok());
        assert!(client_res.unwrap().is_ok());
        let server_res = tokio::time::timeout(std::time::Duration::from_secs(5), server_task).await;
        println!("server_res: {:?}", server_res);
    }

    #[tokio::test]
    async fn frame_data_payload_exceeding_u16_rejected() {
        let (mut client, _server) = tokio::io::duplex(1024);
        let oversized = vec![0u8; 65536];
        let frame = ShellFrame::Data(oversized);
        let res = frame.write_to(&mut client).await;
        assert!(res.is_err());
        assert_eq!(res.unwrap_err().kind(), std::io::ErrorKind::InvalidInput);
    }

    #[tokio::test]
    async fn abandoned_session_releases_after_timeout() {
        use tokio::io::AsyncReadExt;
        use tokio_util::compat::TokioAsyncReadCompatExt;
        let (mut client, server) = tokio::io::duplex(4096);
        let mut server = server.compat();

        let node_info = NodeInfo::local(
            "12D3KooWD3XWsmNtAiqrmFmC6D5gY8QZk4T5D2xSm9R9aZg7kF8h"
                .parse()
                .unwrap(),
            "abandoned-test-node".to_string(),
            fortiq_core::NodeMode::Managed,
        );

        let active_flag = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(true));
        let flag_clone = active_flag.clone();

        let server_task = tokio::spawn(async move {
            send_authorization(&mut server, true).await.unwrap();
            let res = serve(server, node_info).await;
            flag_clone.store(false, std::sync::atomic::Ordering::SeqCst);
            res
        });

        // Client reads the 1-byte authorization
        let mut auth_byte = [0u8; 1];
        client.read_exact(&mut auth_byte).await.unwrap();
        assert_eq!(auth_byte[0], AUTHORIZED);

        // Read the initial banner frame sent by server
        let banner_frame = ShellFrame::read_from(&mut client).await.unwrap();
        assert!(banner_frame.is_some());

        // Now pause the clock and advance past LIVENESS_TIMEOUT (60s) without sending any client frame
        tokio::time::pause();
        tokio::time::advance(std::time::Duration::from_secs(65)).await;
        tokio::time::resume();

        let server_res = tokio::time::timeout(std::time::Duration::from_secs(5), server_task).await;
        assert!(
            server_res.is_ok(),
            "server task should terminate within liveness timeout"
        );
        assert!(
            !active_flag.load(std::sync::atomic::Ordering::SeqCst),
            "session active flag must be released upon abandoned timeout"
        );
    }

    #[tokio::test]
    async fn handshake_encoding_decoding_roundtrip() {
        let (mut client, mut server) = tokio::io::duplex(256);
        let handshake = ShellHandshake::new("TCK-2026-001".to_string());
        handshake.write_to_tokio(&mut client).await.unwrap();

        let received = ShellHandshake::read_from_tokio(&mut server).await.unwrap();
        assert_eq!(received, handshake);
    }
}
