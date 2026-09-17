//! Sovereign Self-Support and Local Loopback Subsystem ("This Device").
//!
//! Implements Phase 12 of FORTIQ Canonical Architecture v3:
//! - Loopback intervention on "This Device" via local IPC without traversing external swarm relays.
//! - Offline self-support ticket creation and local diagnostics collection.
//! - Shell safety gate enforcement and instant client revocation over local loopback.

pub mod ipc;
pub mod session;
pub mod this_device;

#[cfg(test)]
mod tests;

pub use ipc::{LocalIpcError, LocalIpcFramed, LocalIpcMessage, MAX_LOCAL_IPC_FRAME_SIZE};
pub use session::{SelfSupportEngine, SelfSupportError, SelfSupportTicket};
pub use this_device::{
    LocalDiagnostics, LoopbackEndpoint, StorageDiagnostics, SupportTarget, ThisDevice,
};
