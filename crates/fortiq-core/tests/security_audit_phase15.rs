//! Phase 15 — Security Review, Protocol Fuzzing, Chaos, and Verification Suite.
//!
//! Hard Invariants & Specifications:
//! - docs/spec/17-protocol-map-resource-limits.md
//! - docs/spec/20-threat-model.md
//! - docs/spec/21-implementation-roadmap.md (Phase 15)
//! - docs/spec/22-test-plan.md

use fortiq_core::canonical::{
    codec::{from_canonical_cbor, CodecError, DecoderLimits},
    crypto::provider::{CryptoProvider, StandardCryptoProvider},
    deletion::{
        anti_resurrection::AntiResurrectionTracker,
        gc::{PhysicalGarbageCollector, PurgeableStorage},
        purge_auth::PurgeAuthorization,
        tombstone::SignedTombstone,
    },
    portable::{
        certificate::{OperatorCapabilities, OperatorSessionCertificate},
        mnemonic::MnemonicDeriver,
    },
    records::LogicalEvent,
    retirement::authority::{AuthorityError, CanonicalAuthorityResolver},
    self_support::{LocalIpcError, LocalIpcFramed, MAX_LOCAL_IPC_FRAME_SIZE},
    shell::session::EpochRegistry,
    signing::{Signer, SigningError, Verifier},
    storage::{ErasureCoder, ReedSolomonCoder, Shard},
    types::{
        AccessEpoch, EntityId, KeyId, NetworkId, ObjectId, OwnerId, RsProfile, SegmentId, TicketId,
    },
};
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

// ---------------------------------------------------------------------------
// 1. Parser Fuzzing & Resource Abuse Defenses
// ---------------------------------------------------------------------------

#[test]
fn test_fuzz_random_cbor_inputs_never_panic() {
    let limits = DecoderLimits::DEFAULT;
    let mut seed = 0x12345678u64;

    for len in [1, 2, 3, 5, 8, 16, 32, 64, 128, 256, 512, 1024] {
        for _ in 0..50 {
            // Simple PRNG to generate deterministic pseudorandom fuzz bytes
            let mut buf = vec![0u8; len];
            for b in &mut buf {
                seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1);
                *b = (seed >> 33) as u8;
            }

            // Must cleanly return Err, never crash or panic
            let res: Result<LogicalEvent, CodecError> = from_canonical_cbor(&buf, limits);
            assert!(res.is_err());
        }
    }
}

#[test]
fn test_resource_abuse_oversized_payload_rejection() {
    let strict_limits = DecoderLimits {
        max_input_bytes: 512,
        max_depth: 32,
        max_container_len: 256,
        max_bytes_len: 256,
    };

    let oversized = vec![0u8; 1024];
    let result: Result<LogicalEvent, CodecError> = from_canonical_cbor(&oversized, strict_limits);
    assert!(matches!(result, Err(CodecError::InputTooLarge(1024, 512))));
}

#[tokio::test]
async fn test_resource_abuse_local_ipc_oversized_frame_rejection() {
    let (client_io, mut server_io) = tokio::io::duplex(64 * 1024);
    let mut client = LocalIpcFramed::new(client_io);

    use tokio::io::AsyncWriteExt;
    let malicious_len = (MAX_LOCAL_IPC_FRAME_SIZE + 1024) as u32;
    server_io
        .write_all(&malicious_len.to_be_bytes())
        .await
        .unwrap();

    let err = client.recv_message().await.unwrap_err();
    match err {
        LocalIpcError::FrameTooLarge(len) => assert_eq!(len, MAX_LOCAL_IPC_FRAME_SIZE + 1024),
        other => panic!("Expected FrameTooLarge error, got {:?}", other),
    }
}

// ---------------------------------------------------------------------------
// 2. Cryptographic Segmentation & Cross-Segment Isolation
// ---------------------------------------------------------------------------

#[test]
fn test_crypto_segmentation_domain_isolation() {
    use fortiq_core::canonical::crypto::keys::MnemonicEntropy;
    let entropy = MnemonicEntropy::new([0x42; 32]);
    let deriver = MnemonicDeriver::new(&entropy);

    let master_seed = deriver
        .derive_segment_master_seed()
        .expect("Master seed derivation");

    let network_id = NetworkId::from_bytes([0x01; 32]);
    let segment_a = SegmentId::from_bytes([0xAA; 32]);
    let segment_b = SegmentId::from_bytes([0xBB; 32]);

    let crypto = StandardCryptoProvider::new();
    let key_a = crypto
        .derive_segment_secret(&master_seed, &network_id, &segment_a, 1)
        .unwrap();
    let key_b = crypto
        .derive_segment_secret(&master_seed, &network_id, &segment_b, 1)
        .unwrap();

    // Cryptographic isolation: keys for different segments MUST differ
    assert_ne!(key_a.as_bytes(), key_b.as_bytes());

    // Leaking recipient A key cannot unwrap envelope meant for recipient B
    let plaintext = b"Sensitive segment data";
    let aad = b"EventPack:v1";
    let info = b"TicketContext:12345";
    let (_ciphertext, dek) = crypto
        .seal_payload(plaintext, aad)
        .expect("payload seal failed");

    let recipient_a_sk = vec![0x11; 32];
    let recipient_b_pk = vec![0x22; 32];
    let key_id_b = KeyId::from_bytes([0x55; 32]);

    let envelope_b = crypto
        .wrap_dek(&dek, key_id_b, &recipient_b_pk, 1, info)
        .expect("Wrapping succeeds");

    // Attempting unwrap with recipient A private key MUST fail
    let unwrap_err = crypto.unwrap_dek(&envelope_b, &recipient_a_sk, info);
    assert!(unwrap_err.is_err(), "Cross-recipient unwrap must fail");
}

// ---------------------------------------------------------------------------
// 3. Storage Disk Corruption & RS Recovery
// ---------------------------------------------------------------------------

#[test]
fn test_disk_corruption_reed_solomon_self_healing() {
    let profile = RsProfile::new(3, 2); // (3, 2) profile: 3 data, 2 parity
    let coder = ReedSolomonCoder::new();

    let original_data =
        b"FORTIQ sovereign fault-tolerant storage block: all invariants hold perfectly.".to_vec();
    let shards = coder
        .encode(&original_data, profile)
        .expect("Encoding succeeds");
    assert_eq!(shards.len(), 5);

    // Simulate disk corruption: 2 shards corrupted / lost (max tolerable for (3, 2))
    let mut available_shards: Vec<Option<Shard>> = shards.into_iter().map(Some).collect();

    // Drop shard 0 and drop shard 1
    available_shards[0] = None;
    available_shards[1] = None;

    let recovered = coder
        .reconstruct(&available_shards, original_data.len(), profile)
        .expect("Reconstruction with 3 remaining shards must succeed");

    assert_eq!(recovered, original_data);

    // Corrupt 3 shards (exceeds parity capacity): must cleanly fail, never return corrupt data
    available_shards[2] = None;
    let fail = coder.reconstruct(&available_shards, original_data.len(), profile);
    assert!(
        fail.is_err(),
        "Reconstruction with fewer than k shards must fail"
    );
}

// ---------------------------------------------------------------------------
// 4. Anti-Resurrection & Multi-Node Chaos Defenses
// ---------------------------------------------------------------------------

#[test]
fn test_anti_resurrection_stale_node_reannouncement_rejection() {
    let mut tracker = AntiResurrectionTracker::new();
    let signer = MockSigner {
        key_id: KeyId::from_bytes([0x01; 32]),
    };
    let verifier = MockVerifier;

    let target_obj = ObjectId::from_bytes([0x99; 32]);
    let shard_checksum = [0x77; 32];
    let admin = EntityId::from_bytes([0x88; 32]);

    let tombstone =
        SignedTombstone::create(target_obj, admin, "Malicious spam", 1_000, &signer).unwrap();
    let purge_auth = PurgeAuthorization::create(
        tombstone.tombstone_id(),
        vec![target_obj],
        vec![shard_checksum],
        2_000,
        admin,
        &signer,
    )
    .unwrap();

    let mut storage = MockStorage::default();
    storage.objects.insert(target_obj, 512);
    storage.shards.insert(shard_checksum, 1024);

    // Timely purge
    PhysicalGarbageCollector::execute_purge(
        &mut storage,
        &mut tracker,
        &tombstone,
        &purge_auth,
        &verifier,
        2_500,
    )
    .expect("Purge execution succeeds");

    // Stale node partition heals and re-announces target object or shard
    let stale_obj_admission = tracker.check_object_admission(&target_obj);
    assert!(
        stale_obj_admission.is_err(),
        "Purged object must be rejected from re-admission"
    );

    let stale_shard_admission = tracker.check_shard_admission(&shard_checksum);
    assert!(
        stale_shard_admission.is_err(),
        "Purged shard must be rejected from re-admission"
    );
}

#[test]
fn test_shell_revocation_anti_resurrection_under_concurrent_chaos() {
    let mut registry = EpochRegistry::new();
    let ticket_id = TicketId::from_bytes([0x12; 16]);
    let epoch = AccessEpoch::from_bytes([0x34; 16]);

    registry
        .register_epoch(ticket_id, epoch)
        .expect("Register succeeds");
    assert!(registry.is_epoch_valid(&ticket_id, &epoch));

    // Client revokes epoch
    registry.invalidate_epoch(&ticket_id, &epoch);
    assert!(!registry.is_epoch_valid(&ticket_id, &epoch));

    // Stale or malicious peer attempts to re-register the same epoch: rejected
    let re_register_err = registry.register_epoch(ticket_id, epoch);
    assert!(
        re_register_err.is_err(),
        "Revoked epoch can NEVER be resurrected"
    );
}

#[test]
fn test_expired_session_certificate_rejection() {
    let owner_signer = MockSigner {
        key_id: KeyId::from_bytes([0x10; 32]),
    };
    let owner_verifier = MockVerifier;
    let network_id = NetworkId::from_bytes([0x20; 32]);
    let owner_id = OwnerId::from_bytes([0x30; 32]);
    let host_entity = EntityId::from_bytes([0x60; 32]);
    let operator_entity = EntityId::from_bytes([0x50; 32]);
    let operator_key_id = KeyId::from_bytes([0x40; 32]);
    let session_pubkey = [0x70; 32];
    let capabilities = OperatorCapabilities::from_names(["admin"]);
    let nonce = [0x80; 16];

    let issued_at = 1_000;
    let expires_at = 2_000;

    let cert = OperatorSessionCertificate::issue(
        network_id,
        owner_id,
        host_entity,
        operator_entity,
        operator_key_id,
        session_pubkey,
        capabilities,
        issued_at,
        expires_at,
        nonce,
        &owner_signer,
    )
    .unwrap();

    // Valid within range
    assert!(
        CanonicalAuthorityResolver::verify_operator_session(&cert, &owner_verifier, 1_500).is_ok()
    );

    // Expired at current_time = 2001
    let expired_err =
        CanonicalAuthorityResolver::verify_operator_session(&cert, &owner_verifier, 2_001)
            .unwrap_err();
    assert!(matches!(expired_err, AuthorityError::InvalidCertificate(_)));
}
