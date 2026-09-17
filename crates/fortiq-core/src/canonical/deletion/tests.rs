//! Tests for Phase 13 Deletion, Purge Authorization, Anti-Resurrection, and Physical GC.

use super::*;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId, ObjectId};
use std::collections::HashMap;

#[derive(Clone)]
struct MockSigner {
    key_id: KeyId,
}

impl Signer for MockSigner {
    fn sign(&self, domain_separated_data: &[u8]) -> Result<Vec<u8>, SigningError> {
        let hash = blake3::hash(domain_separated_data);
        Ok(hash.as_bytes().to_vec())
    }

    fn key_id(&self) -> KeyId {
        self.key_id
    }
}

struct MockVerifier;

impl Verifier for MockVerifier {
    fn verify(&self, domain_separated_data: &[u8], signature: &[u8]) -> Result<(), SigningError> {
        let hash = blake3::hash(domain_separated_data);
        if hash.as_bytes() == signature {
            Ok(())
        } else {
            Err(SigningError::VerificationFailed(
                "signature mismatch".into(),
            ))
        }
    }
}

#[derive(Default)]
struct MockStorage {
    shards: HashMap<[u8; 32], u64>,
    objects: HashMap<ObjectId, u64>,
}

impl PurgeableStorage for MockStorage {
    fn delete_shard(&mut self, checksum: &[u8; 32]) -> Result<u64, String> {
        Ok(self.shards.remove(checksum).unwrap_or(0))
    }

    fn delete_object(&mut self, object_id: &ObjectId) -> Result<u64, String> {
        Ok(self.objects.remove(object_id).unwrap_or(0))
    }
}

#[test]
fn test_signed_tombstone_lifecycle_and_verification() {
    let signer = MockSigner {
        key_id: KeyId::from_bytes([0x10; 32]),
    };
    let verifier = MockVerifier;
    let target = ObjectId::from_bytes([0xAA; 32]);
    let admin = EntityId::from_bytes([0xBB; 32]);

    let tombstone = SignedTombstone::create(target, admin, "Accidental upload", 1_000, &signer)
        .expect("Tombstone creation must succeed");

    assert!(tombstone.verify(&verifier).is_ok());

    let mut tampered = tombstone.clone();
    tampered.signature[0] ^= 0xFF;
    assert!(tampered.verify(&verifier).is_err());
}

#[test]
fn test_purge_authorization_delayed_enforcement() {
    let signer = MockSigner {
        key_id: KeyId::from_bytes([0x10; 32]),
    };
    let verifier = MockVerifier;
    let tombstone_id = ObjectId::from_bytes([0xCC; 32]);
    let target_obj = ObjectId::from_bytes([0xAA; 32]);
    let shard_a = [0x11; 32];
    let shard_b = [0x22; 32];
    let admin = EntityId::from_bytes([0xBB; 32]);

    let earliest_gc_time = 10_000;
    let purge_auth = PurgeAuthorization::create(
        tombstone_id,
        vec![target_obj],
        vec![shard_a, shard_b],
        earliest_gc_time,
        admin,
        &signer,
    )
    .expect("Purge authorization creation must succeed");

    assert!(purge_auth.verify(&verifier).is_ok());
    assert!(
        !purge_auth.is_ready_for_gc(9_999),
        "GC before earliest time must be forbidden"
    );
    assert!(purge_auth.is_ready_for_gc(10_000));
    assert!(purge_auth.is_ready_for_gc(15_000));
}

#[test]
fn test_physical_gc_execution_and_reclamation() {
    let signer = MockSigner {
        key_id: KeyId::from_bytes([0x10; 32]),
    };
    let verifier = MockVerifier;

    let target_obj = ObjectId::from_bytes([0xAA; 32]);
    let shard_a = [0x11; 32];
    let shard_b = [0x22; 32];
    let admin = EntityId::from_bytes([0xBB; 32]);

    let tombstone =
        SignedTombstone::create(target_obj, admin, "GDPR erasure request", 1_000, &signer).unwrap();
    let tombstone_id = tombstone.tombstone_id();

    let earliest_gc_time = 5_000;
    let purge_auth = PurgeAuthorization::create(
        tombstone_id,
        vec![target_obj],
        vec![shard_a, shard_b],
        earliest_gc_time,
        admin,
        &signer,
    )
    .unwrap();

    let mut storage = MockStorage::default();
    storage.objects.insert(target_obj, 1024);
    storage.shards.insert(shard_a, 4096);
    storage.shards.insert(shard_b, 4096);

    let mut tracker = AntiResurrectionTracker::new();

    // 1. Premature GC fails
    let err = PhysicalGarbageCollector::execute_purge(
        &mut storage,
        &mut tracker,
        &tombstone,
        &purge_auth,
        &verifier,
        4_999,
    )
    .unwrap_err();
    assert!(matches!(
        err,
        GcError::PurgeAuth(PurgeAuthError::PrematureGc { .. })
    ));

    // 2. Timely GC succeeds
    let receipt = PhysicalGarbageCollector::execute_purge(
        &mut storage,
        &mut tracker,
        &tombstone,
        &purge_auth,
        &verifier,
        5_500,
    )
    .expect("GC execution must succeed");

    assert_eq!(receipt.tombstone_id, tombstone_id);
    assert_eq!(receipt.reclaimed_bytes, 1024 + 4096 + 4096);
    assert!(
        storage.objects.is_empty(),
        "Objects must be physically deleted"
    );
    assert!(
        storage.shards.is_empty(),
        "Shards must be physically deleted"
    );

    // 3. Verify anti-resurrection tracker records the purge
    assert!(tracker.is_object_purged(&target_obj));
    assert!(tracker.is_shard_purged(&shard_a));
    assert!(tracker.is_shard_purged(&shard_b));

    // Stale peer re-announcement of purged object or shard must be strictly rejected
    assert_eq!(
        tracker.check_object_admission(&target_obj),
        Err(AntiResurrectionError::ObjectPurged(target_obj))
    );
    assert_eq!(
        tracker.check_shard_admission(&shard_a),
        Err(AntiResurrectionError::ShardPurged(shard_a))
    );
}

#[test]
fn test_anti_resurrection_tombstone_admission_check() {
    let mut tracker = AntiResurrectionTracker::new();
    let target = ObjectId::from_bytes([0xDD; 32]);
    let tombstone_id = ObjectId::from_bytes([0xEE; 32]);

    assert!(tracker.check_object_admission(&target).is_ok());

    tracker.record_tombstone(tombstone_id, target);
    assert!(tracker.is_tombstoned(&target));
    assert_eq!(
        tracker.check_object_admission(&target),
        Err(AntiResurrectionError::ObjectTombstoned(target))
    );
}
