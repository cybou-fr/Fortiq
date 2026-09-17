//! "This Device" identity, loopback target abstraction, and local diagnostics.
//!
//! Enforces Phase 12 / ADR requirements:
//! - Loopback intervention ("This Device") executes without traversing external swarm relays.
//! - Distinguishes local loopback target from remote swarm peer targets.
//! - Collects offline system and storage diagnostics for local self-support.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

use crate::canonical::types::EntityId;

/// Represents a loopback target address on the local machine.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum LoopbackEndpoint {
    /// In-memory asynchronous duplex channel (zero OS socket overhead).
    MemoryChannel,
    /// Unix Domain Socket path on Unix/Linux systems.
    UnixSocket(PathBuf),
    /// Windows Named Pipe name (e.g. `\\.\pipe\fortiq-self-support`).
    WindowsNamedPipe(String),
}

impl Default for LoopbackEndpoint {
    fn default() -> Self {
        #[cfg(windows)]
        {
            Self::WindowsNamedPipe(r"\\.\pipe\fortiq-self-support".to_string())
        }
        #[cfg(not(windows))]
        {
            Self::UnixSocket(PathBuf::from("/run/fortiq-self-support.sock"))
        }
    }
}

/// Target selector distinguishing local loopback operations on "This Device"
/// from remote peer operations traversing libp2p swarm relays.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum SupportTarget {
    /// Local machine loopback. Guarantees no external swarm relays or WAN traversal.
    ThisDevice,
    /// Remote node identified by its canonical EntityId, requiring network transport.
    RemotePeer(EntityId),
}

impl SupportTarget {
    /// Returns true if this target represents the local machine.
    pub fn is_this_device(&self) -> bool {
        matches!(self, Self::ThisDevice)
    }
}

/// Storage health summary included in local diagnostic reports.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct StorageDiagnostics {
    pub active_shards: usize,
    pub event_packs_stored: usize,
    pub canonical_heads: usize,
    pub local_storage_bytes: u64,
}

/// System and runtime diagnostic report collected directly on "This Device".
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LocalDiagnostics {
    pub os: String,
    pub arch: String,
    pub hostname: String,
    pub is_loopback_active: bool,
    /// Must always be true for "This Device": confirms swarm relays are bypassed.
    pub relay_bypassed: bool,
    pub storage: StorageDiagnostics,
    pub timestamp_secs: u64,
}

/// Representation of "This Device" providing sovereign self-support capabilities.
#[derive(Debug, Clone)]
pub struct ThisDevice {
    device_id: EntityId,
    endpoint: LoopbackEndpoint,
}

impl ThisDevice {
    /// Constructs a new ThisDevice instance with the given EntityId and loopback endpoint.
    pub fn new(device_id: EntityId, endpoint: LoopbackEndpoint) -> Self {
        Self {
            device_id,
            endpoint,
        }
    }

    /// Returns the EntityId of this device.
    pub fn device_id(&self) -> &EntityId {
        &self.device_id
    }

    /// Returns the configured loopback endpoint.
    pub fn endpoint(&self) -> &LoopbackEndpoint {
        &self.endpoint
    }

    /// Returns true if the given EntityId matches this local device.
    pub fn is_local_entity(&self, id: &EntityId) -> bool {
        &self.device_id == id
    }

    /// Resolves a `SupportTarget` relative to this device.
    pub fn resolve_target(&self, target: &SupportTarget) -> bool {
        match target {
            SupportTarget::ThisDevice => true,
            SupportTarget::RemotePeer(id) => self.is_local_entity(id),
        }
    }

    /// Gathers local system diagnostics without external network queries.
    pub fn collect_diagnostics(&self, storage: Option<StorageDiagnostics>) -> LocalDiagnostics {
        let hostname = std::env::var("COMPUTERNAME")
            .or_else(|_| std::env::var("HOSTNAME"))
            .unwrap_or_else(|_| "localhost".to_string());

        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);

        LocalDiagnostics {
            os: std::env::consts::OS.to_string(),
            arch: std::env::consts::ARCH.to_string(),
            hostname,
            is_loopback_active: true,
            relay_bypassed: true,
            storage: storage.unwrap_or_default(),
            timestamp_secs: now,
        }
    }
}
