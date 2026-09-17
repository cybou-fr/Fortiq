use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

pub const MAX_LOG_ENTRIES: usize = 500;

#[derive(Clone, Default)]
pub struct DiagnosticLogger {
    entries: Arc<Mutex<VecDeque<String>>>,
}

pub fn init() -> DiagnosticLogger {
    DiagnosticLogger::new()
}

impl DiagnosticLogger {
    pub fn new() -> Self {
        Self {
            entries: Arc::new(Mutex::new(VecDeque::with_capacity(MAX_LOG_ENTRIES))),
        }
    }

    pub fn log(&self, message: impl Into<String>) {
        let msg = message.into();
        let timestamp = chrono_like_now();
        let formatted = format!("[{timestamp}] {msg}");
        let mut guard = self.entries.lock().unwrap();
        if guard.len() >= MAX_LOG_ENTRIES {
            guard.pop_front();
        }
        guard.push_back(formatted);
    }

    pub fn get_recent_logs(&self) -> Vec<String> {
        let guard = self.entries.lock().unwrap();
        guard.iter().cloned().collect()
    }
}

fn chrono_like_now() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    format!("{now}")
}
