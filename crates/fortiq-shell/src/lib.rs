use std::path::Path;

#[cfg(any(target_os = "linux", target_os = "windows"))]
use std::process::Stdio;

use anyhow::{Context, Result};
use fortiq_core::NodeInfo;
use futures::{
    AsyncRead, AsyncReadExt as FuturesAsyncReadExt, AsyncWrite,
    AsyncWriteExt as FuturesAsyncWriteExt,
};
#[cfg(any(target_os = "linux", target_os = "windows"))]
use tokio::io::AsyncReadExt;
use tokio::io::AsyncWriteExt;
use tokio_util::compat::FuturesAsyncReadCompatExt;

const AUTHORIZED: u8 = 1;
const DENIED: u8 = 0;

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
    std::env::var_os("PATH").is_some_and(|path| {
        std::env::split_paths(&path).any(|directory| directory.join(program).is_file())
    })
}

pub async fn serve<S>(stream: S, local_info: NodeInfo) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    #[cfg(target_os = "linux")]
    {
        serve_linux(stream, local_info).await
    }

    #[cfg(target_os = "windows")]
    {
        serve_windows(stream, local_info).await
    }

    #[cfg(not(any(target_os = "linux", target_os = "windows")))]
    {
        let _ = (stream, local_info);
        anyhow::bail!("remote shell serving is not implemented on this operating system")
    }
}

pub async fn send_authorization<S>(stream: &mut S, allowed: bool) -> Result<()>
where
    S: AsyncWrite + Unpin,
{
    stream
        .write_all(&[if allowed { AUTHORIZED } else { DENIED }])
        .await?;
    stream.flush().await?;
    Ok(())
}

#[cfg(target_os = "linux")]
async fn serve_linux<S>(stream: S, local_info: NodeInfo) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let shell = detect_linux_shell()?;
    let child = tokio::process::Command::new(shell.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
        .with_context(|| format!("failed to launch {}", shell.path()))?;

    serve_child(stream, local_info, shell, child).await
}

#[cfg(target_os = "windows")]
async fn serve_windows<S>(stream: S, local_info: NodeInfo) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let shell = detect_windows_shell()?;
    let child = tokio::process::Command::new(shell.program())
        .args(shell.arguments())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
        .with_context(|| format!("failed to launch {}", shell.program()))?;

    serve_child(stream, local_info, shell, child).await
}

#[cfg(any(target_os = "linux", target_os = "windows"))]
async fn serve_child<S>(
    stream: S,
    local_info: NodeInfo,
    shell: ShellKind,
    mut child: tokio::process::Child,
) -> Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let child_stdin = child.stdin.take().context("shell stdin unavailable")?;
    let mut child_stdout = child.stdout.take().context("shell stdout unavailable")?;
    let mut child_stderr = child.stderr.take().context("shell stderr unavailable")?;
    let (mut network_read, mut network_write) = tokio::io::split(stream.compat());

    let banner = format!(
        "FORTIQ Remote Session\n\nHost: {}\nPeerId: {}\nOS: {}\nArch: {}\nShell: {}\nFORTIQ: {}\n\n",
        local_info.name,
        local_info.peer_id,
        local_info.os,
        local_info.arch,
        shell.name(),
        local_info.version
    );
    network_write.write_all(banner.as_bytes()).await?;
    network_write.flush().await?;

    let input_task = tokio::spawn(async move {
        let mut child_stdin = child_stdin;
        tokio::io::copy(&mut network_read, &mut child_stdin).await?;
        child_stdin.shutdown().await
    });

    let output_result =
        copy_shell_output(&mut child_stdout, &mut child_stderr, &mut network_write).await;
    input_task.abort();
    let _ = input_task.await;

    if child.try_wait()?.is_none() {
        child.kill().await.context("failed to terminate shell")?;
    }
    child.wait().await.context("failed to reap shell process")?;
    output_result
}

#[cfg(any(target_os = "linux", target_os = "windows"))]
async fn copy_shell_output<W>(
    stdout: &mut tokio::process::ChildStdout,
    stderr: &mut tokio::process::ChildStderr,
    writer: &mut W,
) -> Result<()>
where
    W: tokio::io::AsyncWrite + Unpin,
{
    let mut stdout_open = true;
    let mut stderr_open = true;
    let mut stdout_buffer = [0_u8; 8192];
    let mut stderr_buffer = [0_u8; 8192];

    while stdout_open || stderr_open {
        tokio::select! {
            read = stdout.read(&mut stdout_buffer), if stdout_open => {
                let count = read.context("failed to read shell stdout")?;
                stdout_open = count != 0;
                if count != 0 {
                    writer.write_all(&stdout_buffer[..count]).await?;
                    writer.flush().await?;
                }
            }
            read = stderr.read(&mut stderr_buffer), if stderr_open => {
                let count = read.context("failed to read shell stderr")?;
                stderr_open = count != 0;
                if count != 0 {
                    writer.write_all(&stderr_buffer[..count]).await?;
                    writer.flush().await?;
                }
            }
        }
    }
    writer.shutdown().await?;
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
        remote_write.write_all(command.as_bytes()).await?;
        remote_write.write_all(b"\nexit\n").await?;
        remote_write.shutdown().await?;
        tokio::io::copy(&mut remote_read, &mut tokio::io::stdout()).await?;
        return Ok(());
    }

    let upload = tokio::spawn(async move {
        tokio::io::copy(&mut tokio::io::stdin(), &mut remote_write).await?;
        remote_write.shutdown().await
    });
    tokio::io::copy(&mut remote_read, &mut tokio::io::stdout()).await?;
    upload.abort();
    let _ = upload.await;
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
}
