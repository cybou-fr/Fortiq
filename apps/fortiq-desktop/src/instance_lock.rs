use fs2::FileExt;
use std::fs::{File, OpenOptions};
use std::path::PathBuf;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum InstanceLockError {
    #[error("Another instance of FORTIQ Desktop is already running")]
    AlreadyRunning,
    #[error("Failed to acquire lock file at {path}: {source}")]
    Io {
        path: PathBuf,
        source: std::io::Error,
    },
}

#[derive(Debug)]
pub struct InstanceLock {
    _file: File,
    path: PathBuf,
}

impl InstanceLock {
    pub fn acquire() -> Result<Self, InstanceLockError> {
        let path = lock_file_path();
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }

        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&path)
            .map_err(|source| InstanceLockError::Io {
                path: path.clone(),
                source,
            })?;

        match file.try_lock_exclusive() {
            Ok(()) => Ok(Self { _file: file, path }),
            Err(e) => {
                if e.kind() == std::io::ErrorKind::WouldBlock {
                    Err(InstanceLockError::AlreadyRunning)
                } else {
                    Err(InstanceLockError::Io { path, source: e })
                }
            }
        }
    }

    pub fn path(&self) -> &std::path::Path {
        &self.path
    }
}

fn lock_file_path() -> PathBuf {
    #[cfg(windows)]
    {
        if let Some(local_app_data) = std::env::var_os("LOCALAPPDATA") {
            return PathBuf::from(local_app_data)
                .join("FORTIQ")
                .join("fortiq-desktop.lock");
        }
    }

    #[cfg(unix)]
    {
        if let Some(runtime_dir) = std::env::var_os("XDG_RUNTIME_DIR") {
            return PathBuf::from(runtime_dir).join("fortiq-desktop.lock");
        }
    }

    std::env::temp_dir().join("fortiq-desktop.lock")
}
