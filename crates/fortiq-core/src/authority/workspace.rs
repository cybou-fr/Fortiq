use crate::object::{NetworkId, OwnerId};
use super::mnemonic::OwnerSegmentMasterSeed;

/// Ephemeral in-memory operator workspace holding decrypted data and segment secrets.
#[derive(Default)]
pub struct MemoryWorkspace {
    network_id: Option<NetworkId>,
    owner_id: Option<OwnerId>,
    segment_master_seed: Option<OwnerSegmentMasterSeed>,
    unlocked: bool,
}

impl MemoryWorkspace {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn unlock(
        &mut self,
        network_id: NetworkId,
        owner_id: OwnerId,
        segment_master_seed: OwnerSegmentMasterSeed,
    ) {
        self.lock();
        self.network_id = Some(network_id);
        self.owner_id = Some(owner_id);
        self.segment_master_seed = Some(segment_master_seed);
        self.unlocked = true;
    }

    pub fn is_unlocked(&self) -> bool {
        self.unlocked
    }

    pub fn network_id(&self) -> Option<&NetworkId> {
        self.network_id.as_ref()
    }

    pub fn owner_id(&self) -> Option<&OwnerId> {
        self.owner_id.as_ref()
    }

    pub fn lock(&mut self) {
        self.segment_master_seed = None;
        self.network_id = None;
        self.owner_id = None;
        self.unlocked = false;
    }

    pub fn wipe(&mut self) {
        self.lock();
    }
}

impl Drop for MemoryWorkspace {
    fn drop(&mut self) {
        self.lock();
    }
}
