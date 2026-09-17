//! Memory-Only Decrypted Workspace and Operator Lock/Wipe.
//!
//! Hard Invariants & Specifications (docs/spec/03-genesis-owner-key-lifecycle.md, docs/spec/16-local-persistence-caching.md):
//! - Zero disk footprint: decrypted tickets, chat, and derived segment secrets exist only in memory.
//! - Cleared and zeroized immediately on Operator Lock or Drop.
//! - OwnerSegmentMasterSeed remains only during the unlocked session.
//! - Once locked, all sensitive material is wiped and all operations fail until re-unlocked.

use std::collections::HashMap;
use thiserror::Error;

use crate::canonical::crypto::keys::{DerivedSegmentSecret, OwnerSegmentMasterSeed};
use crate::canonical::crypto::provider::{CryptoError, CryptoProvider};
use crate::canonical::events::reducer::TicketView;
use crate::canonical::types::{NetworkId, OwnerId, SegmentId, TicketId};

#[derive(Error, Debug)]
pub enum WorkspaceError {
    #[error("workspace is locked; mnemonic unlock required")]
    WorkspaceLocked,
    #[error("crypto error: {0}")]
    Crypto(#[from] CryptoError),
}

/// Ephemeral in-memory operator workspace holding decrypted data and segment secrets.
#[derive(Default)]
pub struct MemoryWorkspace {
    network_id: Option<NetworkId>,
    owner_id: Option<OwnerId>,
    segment_master_seed: Option<OwnerSegmentMasterSeed>,
    derived_segment_secrets: HashMap<SegmentId, DerivedSegmentSecret>,
    decrypted_tickets: HashMap<TicketId, TicketView>,
    unlocked: bool,
}

impl MemoryWorkspace {
    /// Creates an initially locked, empty workspace.
    pub fn new() -> Self {
        Self::default()
    }

    /// Unlocks the workspace with the operator's derived master segment seed.
    pub fn unlock(
        &mut self,
        network_id: NetworkId,
        owner_id: OwnerId,
        segment_master_seed: OwnerSegmentMasterSeed,
    ) {
        // Lock and clear any previous state first
        self.lock();

        self.network_id = Some(network_id);
        self.owner_id = Some(owner_id);
        self.segment_master_seed = Some(segment_master_seed);
        self.unlocked = true;
    }

    /// Returns true if the workspace is currently unlocked.
    pub fn is_unlocked(&self) -> bool {
        self.unlocked
    }

    /// Derives and caches a client segment secret on demand using the master segment seed.
    pub fn derive_segment_secret(
        &mut self,
        segment_id: &SegmentId,
        key_epoch: u64,
        provider: &impl CryptoProvider,
    ) -> Result<DerivedSegmentSecret, WorkspaceError> {
        if !self.unlocked {
            return Err(WorkspaceError::WorkspaceLocked);
        }

        let master_seed = self
            .segment_master_seed
            .as_ref()
            .ok_or(WorkspaceError::WorkspaceLocked)?;
        let network_id = self
            .network_id
            .as_ref()
            .ok_or(WorkspaceError::WorkspaceLocked)?;

        let secret =
            provider.derive_segment_secret(master_seed, network_id, segment_id, key_epoch)?;

        self.derived_segment_secrets
            .insert(*segment_id, secret.clone());
        Ok(secret)
    }

    /// Stores a decrypted ticket view in the memory-only cache.
    pub fn cache_ticket(&mut self, view: TicketView) -> Result<(), WorkspaceError> {
        if !self.unlocked {
            return Err(WorkspaceError::WorkspaceLocked);
        }
        self.decrypted_tickets.insert(view.ticket_id, view);
        Ok(())
    }

    /// Retrieves a cached decrypted ticket view.
    pub fn get_ticket(&self, ticket_id: &TicketId) -> Result<Option<&TicketView>, WorkspaceError> {
        if !self.unlocked {
            return Err(WorkspaceError::WorkspaceLocked);
        }
        Ok(self.decrypted_tickets.get(ticket_id))
    }

    /// Locks and securely wipes all sensitive in-memory decrypted data and master seeds.
    pub fn lock(&mut self) {
        // Drop master seed and derived secrets (triggers ZeroizeOnDrop)
        self.segment_master_seed = None;
        self.derived_segment_secrets.clear();

        // Clear decrypted tickets
        self.decrypted_tickets.clear();

        self.network_id = None;
        self.owner_id = None;
        self.unlocked = false;
    }

    /// Complete memory wipe and disposal of workspace.
    pub fn wipe(&mut self) {
        self.lock();
    }
}

impl Drop for MemoryWorkspace {
    fn drop(&mut self) {
        self.lock();
    }
}
