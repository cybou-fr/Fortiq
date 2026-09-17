use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DesktopSettings {
    pub refresh_interval_secs: u64,
    pub theme_mode: String,
    pub terminal_font_size: u32,
    pub last_ticket_id: Option<String>,
    pub notifications_enabled: bool,
    pub daemon_pipe: Option<String>,
    pub minimize_to_tray: bool,
}

impl Default for DesktopSettings {
    fn default() -> Self {
        Self {
            refresh_interval_secs: 3,
            theme_mode: "system".to_string(),
            terminal_font_size: 14,
            last_ticket_id: None,
            notifications_enabled: true,
            daemon_pipe: None,
            minimize_to_tray: true,
        }
    }
}

impl DesktopSettings {
    pub fn config_path() -> PathBuf {
        #[cfg(windows)]
        {
            if let Ok(local_app_data) = std::env::var("LOCALAPPDATA") {
                PathBuf::from(local_app_data)
                    .join("FORTIQ")
                    .join("desktop.toml")
            } else {
                PathBuf::from(r"C:\ProgramData\FORTIQ\desktop.toml")
            }
        }
        #[cfg(unix)]
        {
            if let Ok(home) = std::env::var("HOME") {
                PathBuf::from(home)
                    .join(".config")
                    .join("fortiq")
                    .join("desktop.toml")
            } else {
                PathBuf::from("/etc/fortiq/desktop.toml")
            }
        }
        #[cfg(not(any(windows, unix)))]
        {
            PathBuf::from("desktop.toml")
        }
    }

    pub fn load() -> Self {
        let path = Self::config_path();
        if let Ok(content) = std::fs::read_to_string(&path) {
            toml::from_str(&content).unwrap_or_default()
        } else {
            Self::default()
        }
    }

    pub fn save(&self) -> Result<(), std::io::Error> {
        let path = Self::config_path();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let serialized = toml::to_string_pretty(self)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        std::fs::write(&path, serialized)
    }
}
