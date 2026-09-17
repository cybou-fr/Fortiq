use std::collections::{HashMap, HashSet};
use std::sync::RwLock;

use crate::canonical::control::{
    ControlError, DeviceBinding, EnrollmentCertificate, Genesis, RevocationList, SegmentDescriptor,
};
use crate::canonical::types::{EntityId, KeyId, NetworkId, SegmentId};

/// Thread-safe in-memory store for verified Root Control Plane objects.
pub struct ControlStore {
    genesis: Genesis,
    genesis_id: [u8; 32],
    segment_descriptors: RwLock<HashMap<SegmentId, SegmentDescriptor>>,
    enrollments: RwLock<HashMap<EntityId, EnrollmentCertificate>>,
    device_bindings: RwLock<HashMap<EntityId, DeviceBinding>>,
    revoked_entities: RwLock<HashSet<EntityId>>,
    revoked_keys: RwLock<HashSet<KeyId>>,
    revocation_epoch: RwLock<u64>,
}

impl ControlStore {
    /// Initialize a new ControlStore rooted in a verified Genesis.
    pub fn new(genesis: Genesis) -> Result<Self, ControlError> {
        let genesis_id = genesis.genesis_id()?;
        Ok(Self {
            genesis,
            genesis_id,
            segment_descriptors: RwLock::new(HashMap::new()),
            enrollments: RwLock::new(HashMap::new()),
            device_bindings: RwLock::new(HashMap::new()),
            revoked_entities: RwLock::new(HashSet::new()),
            revoked_keys: RwLock::new(HashSet::new()),
            revocation_epoch: RwLock::new(0),
        })
    }

    pub fn network_id(&self) -> NetworkId {
        self.genesis.tbs.network_id
    }

    pub fn genesis(&self) -> &Genesis {
        &self.genesis
    }

    pub fn genesis_id(&self) -> &[u8; 32] {
        &self.genesis_id
    }

    /// Register a verified SegmentDescriptor.
    pub fn insert_segment_descriptor(
        &self,
        descriptor: SegmentDescriptor,
    ) -> Result<(), ControlError> {
        if descriptor.network_id != self.network_id() {
            return Err(ControlError::MismatchedNetwork(
                self.network_id(),
                descriptor.network_id,
            ));
        }

        let mut segments = self.segment_descriptors.write().unwrap();
        segments.insert(descriptor.segment_id, descriptor);
        Ok(())
    }

    /// Retrieve a SegmentDescriptor by SegmentId.
    pub fn get_segment_descriptor(&self, segment_id: &SegmentId) -> Option<SegmentDescriptor> {
        let segments = self.segment_descriptors.read().unwrap();
        segments.get(segment_id).cloned()
    }

    /// Register a verified EnrollmentCertificate.
    pub fn insert_enrollment(&self, cert: EnrollmentCertificate) -> Result<(), ControlError> {
        if cert.network_id != self.network_id() {
            return Err(ControlError::MismatchedNetwork(
                self.network_id(),
                cert.network_id,
            ));
        }

        // Check if entity is already revoked
        if self.is_entity_revoked(&cert.entity_id) {
            return Err(ControlError::EntityRevoked(cert.entity_id));
        }

        let mut enrollments = self.enrollments.write().unwrap();
        enrollments.insert(cert.entity_id, cert);
        Ok(())
    }

    /// Retrieve an EnrollmentCertificate by EntityId.
    pub fn get_enrollment(&self, entity_id: &EntityId) -> Option<EnrollmentCertificate> {
        let enrollments = self.enrollments.read().unwrap();
        enrollments.get(entity_id).cloned()
    }

    /// Register an active DeviceBinding.
    pub fn insert_device_binding(&self, binding: DeviceBinding) -> Result<(), ControlError> {
        if binding.network_id != self.network_id() {
            return Err(ControlError::MismatchedNetwork(
                self.network_id(),
                binding.network_id,
            ));
        }

        if self.is_entity_revoked(&binding.entity_id) {
            return Err(ControlError::EntityRevoked(binding.entity_id));
        }

        let mut bindings = self.device_bindings.write().unwrap();
        bindings.insert(binding.entity_id, binding);
        Ok(())
    }

    /// Retrieve an active DeviceBinding by EntityId.
    pub fn get_device_binding(&self, entity_id: &EntityId) -> Option<DeviceBinding> {
        let bindings = self.device_bindings.read().unwrap();
        bindings.get(entity_id).cloned()
    }

    /// Apply an authoritative RevocationList enforcing monotonic epoch advancement.
    pub fn apply_revocations(&self, rev: RevocationList) -> Result<(), ControlError> {
        if rev.network_id != self.network_id() {
            return Err(ControlError::MismatchedNetwork(
                self.network_id(),
                rev.network_id,
            ));
        }

        let mut epoch = self.revocation_epoch.write().unwrap();
        if rev.revocation_epoch <= *epoch {
            return Err(ControlError::StaleRevocationEpoch(
                *epoch,
                rev.revocation_epoch,
            ));
        }

        *epoch = rev.revocation_epoch;

        let mut revoked_entities = self.revoked_entities.write().unwrap();
        for entity in rev.revoked_entities {
            revoked_entities.insert(entity);
        }

        let mut revoked_keys = self.revoked_keys.write().unwrap();
        for key in rev.revoked_keys {
            revoked_keys.insert(key);
        }

        Ok(())
    }

    /// Check if an EntityId is revoked.
    pub fn is_entity_revoked(&self, entity_id: &EntityId) -> bool {
        let revoked = self.revoked_entities.read().unwrap();
        revoked.contains(entity_id)
    }

    /// Check if a KeyId is revoked.
    pub fn is_key_revoked(&self, key_id: &KeyId) -> bool {
        let revoked = self.revoked_keys.read().unwrap();
        revoked.contains(key_id)
    }

    /// Current monotonic revocation epoch.
    pub fn current_revocation_epoch(&self) -> u64 {
        *self.revocation_epoch.read().unwrap()
    }
}
