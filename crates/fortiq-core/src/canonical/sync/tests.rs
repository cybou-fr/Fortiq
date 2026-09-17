use std::collections::HashMap;

use crate::canonical::crypto::keys::DataEncryptionKey;
use crate::canonical::events::graph::{EventGraph, VerifiedEventPack};
use crate::canonical::events::snapshot::{reduce_ticket_from_snapshot, TicketSnapshot};
use crate::canonical::records::LogicalEvent;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::sync::head::{HeadAdvertisement, HeadError, HeadTracker, SegmentHeadIndex};
use crate::canonical::sync::inventory::{AntiEntropyEngine, InventoryResponse};
use crate::canonical::sync::snapshot::EncryptedSnapshot;
use crate::canonical::sync::tail::{
    PackHeaderInfo, TailSyncError, TailSyncPlanner, TailSyncStatus,
};
use crate::canonical::types::{KeyId, NetworkId, ObjectId, SegmentId, StreamId, TicketId};

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

#[test]
fn test_head_advertisement_signing_verification_and_expiry() {
    let key_id = KeyId::from_bytes([0x44; 32]);
    let signer = MockSigner { key_id };
    let verifier = MockVerifier;

    let net_id = NetworkId::from_bytes([1u8; 32]);
    let seg_id = SegmentId::from_bytes([2u8; 32]);
    let stream_id = StreamId::from_bytes([3u8; 16]);
    let pack_id = ObjectId::from_bytes([4u8; 32]);
    let expires_at = 10_000u64;

    let adv = HeadAdvertisement::create_and_sign(
        net_id, seg_id, stream_id, 1, pack_id, expires_at, &signer,
    )
    .expect("signing head advertisement must succeed");

    assert_eq!(adv.writer_key_id, key_id);

    // 1. Valid verification at current_time = 5000
    adv.verify(&verifier, 5000)
        .expect("verification before expiry must succeed");

    // 2. Expired verification at current_time = 10001
    let err = adv.verify(&verifier, 10001).unwrap_err();
    assert_eq!(err, HeadError::Expired(10000, 10001));

    // 3. Tampered signature fails
    let mut tampered = adv.clone();
    tampered.signature[0] ^= 0xFF;
    assert!(tampered.verify(&verifier, 5000).is_err());
}

#[test]
fn test_head_tracker_monotonic_updates_and_fork_detection() {
    let signer = MockSigner {
        key_id: KeyId::from_bytes([0x44; 32]),
    };
    let mut tracker = HeadTracker::new();

    let net_id = NetworkId::from_bytes([1u8; 32]);
    let seg_id = SegmentId::from_bytes([2u8; 32]);
    let stream_id = StreamId::from_bytes([3u8; 16]);
    let pack1 = ObjectId::from_bytes([10u8; 32]);
    let pack2 = ObjectId::from_bytes([20u8; 32]);
    let pack2_fork = ObjectId::from_bytes([21u8; 32]);

    let adv1 =
        HeadAdvertisement::create_and_sign(net_id, seg_id, stream_id, 1, pack1, 10_000, &signer)
            .unwrap();

    let adv2 =
        HeadAdvertisement::create_and_sign(net_id, seg_id, stream_id, 2, pack2, 10_000, &signer)
            .unwrap();

    let adv2_fork = HeadAdvertisement::create_and_sign(
        net_id, seg_id, stream_id, 2, pack2_fork, 10_000, &signer,
    )
    .unwrap();

    // 1. Insert initial seq 1
    tracker.update_head(adv1.clone(), 5000).unwrap();
    assert_eq!(tracker.get_head(&stream_id).unwrap().writer_seq, 1);

    // 2. Monotonic update to seq 2 succeeds
    tracker.update_head(adv2, 5000).unwrap();
    assert_eq!(tracker.get_head(&stream_id).unwrap().writer_seq, 2);

    // 3. Stale sequence update (seq 1 when seq 2 known) is rejected
    let err_stale = tracker.update_head(adv1, 5000).unwrap_err();
    assert_eq!(err_stale, HeadError::StaleSequence(1, 2));

    // 4. Fork detection: same sequence 2 but different pack ID!
    let err_fork = tracker.update_head(adv2_fork, 5000).unwrap_err();
    assert_eq!(err_fork, HeadError::ForkDetected(stream_id, 2));

    // 5. Segment heads index query
    let segment_heads = tracker.heads_for_segment(&seg_id);
    assert_eq!(segment_heads.len(), 1);
    let index = SegmentHeadIndex {
        segment_id: seg_id,
        page_index: 0,
        heads: segment_heads,
    };
    assert_eq!(index.heads[0].writer_seq, 2);
}

#[test]
fn test_tail_sync_backward_walk_and_forward_reduction_plan() {
    let stream_id = StreamId::from_bytes([5u8; 16]);
    let p1 = ObjectId::from_bytes([1u8; 32]);
    let p2 = ObjectId::from_bytes([2u8; 32]);
    let p3 = ObjectId::from_bytes([3u8; 32]);
    let p4 = ObjectId::from_bytes([4u8; 32]);

    let mut headers = HashMap::new();
    headers.insert(
        p1,
        PackHeaderInfo {
            pack_id: p1,
            stream_id,
            writer_seq: 1,
            prev_pack_id: None,
        },
    );
    headers.insert(
        p2,
        PackHeaderInfo {
            pack_id: p2,
            stream_id,
            writer_seq: 2,
            prev_pack_id: Some(p1),
        },
    );
    headers.insert(
        p3,
        PackHeaderInfo {
            pack_id: p3,
            stream_id,
            writer_seq: 3,
            prev_pack_id: Some(p2),
        },
    );
    headers.insert(
        p4,
        PackHeaderInfo {
            pack_id: p4,
            stream_id,
            writer_seq: 4,
            prev_pack_id: Some(p3),
        },
    );

    let planner = TailSyncPlanner::default();

    // Scenario: Local node already knows up to p2
    let local_known = |id: ObjectId| id == p1 || id == p2;
    let fetch_fn = |id: ObjectId| headers.get(&id).cloned();

    let status = planner
        .plan_tail_sync(stream_id, Some((2, p2)), (4, p4), fetch_fn, local_known)
        .expect("tail sync planning should succeed");

    match status {
        TailSyncStatus::NeedsCatchUp {
            remote_seq,
            local_seq,
            missing_packs_forward,
        } => {
            assert_eq!(remote_seq, 4);
            assert_eq!(local_seq, 2);
            // Missing packs in forward reduction order: p3, then p4
            assert_eq!(missing_packs_forward, vec![p3, p4]);
        }
        TailSyncStatus::UpToDate => panic!("expected NeedsCatchUp"),
    }

    // When remote head matches local head
    let up_to_date = planner
        .plan_tail_sync(stream_id, Some((4, p4)), (4, p4), fetch_fn, local_known)
        .expect("tail sync up-to-date should succeed");
    assert_eq!(up_to_date, TailSyncStatus::UpToDate);
}

#[test]
fn test_tail_sync_stream_fork_detected() {
    let stream_id = StreamId::from_bytes([5u8; 16]);
    let p2_local = ObjectId::from_bytes([20u8; 32]);
    let p2_remote_fork = ObjectId::from_bytes([21u8; 32]);

    let planner = TailSyncPlanner::default();

    let result = planner.plan_tail_sync(
        stream_id,
        Some((2, p2_local)),
        (2, p2_remote_fork),
        |_| None,
        |_| false,
    );

    match result {
        Err(TailSyncError::StreamForkDetected(s, r, l)) => {
            assert_eq!(s, stream_id);
            assert_eq!(r, p2_remote_fork);
            assert_eq!(l, p2_local);
        }
        _ => panic!("expected StreamForkDetected error"),
    }
}

#[test]
fn test_anti_entropy_inventory_reconciliation() {
    let seg_id = SegmentId::from_bytes([1u8; 32]);

    let p1 = ObjectId::from_bytes([1u8; 32]);
    let p2 = ObjectId::from_bytes([2u8; 32]);
    let p3 = ObjectId::from_bytes([3u8; 32]);

    let m1 = ObjectId::from_bytes([10u8; 32]);
    let m2 = ObjectId::from_bytes([20u8; 32]);

    let t1 = ObjectId::from_bytes([99u8; 32]);

    let local_packs = vec![p1, p2];
    let local_manifests = vec![m1];
    let local_tombstones = vec![];

    let remote_response = InventoryResponse::new(
        seg_id,
        vec![p2, p3], // remote has p2 and p3
        vec![m2],     // remote has m2
        vec![t1],     // remote has tombstone t1
        1,
    );

    let plan = AntiEntropyEngine::reconcile(
        &local_packs,
        &local_manifests,
        &local_tombstones,
        &remote_response,
    );

    assert_eq!(plan.packs_to_pull, vec![p3]);
    assert_eq!(plan.packs_to_push, vec![p1]);
    assert_eq!(plan.manifests_to_pull, vec![m2]);
    assert_eq!(plan.manifests_to_push, vec![m1]);
    assert_eq!(plan.tombstones_to_pull, vec![t1]);
    assert!(!plan.is_empty());
}

#[test]
fn test_encrypted_snapshot_seal_open_and_fast_tail_catchup() {
    use crate::canonical::events::reducer::reduce_ticket_with_resolver;
    use crate::canonical::events::reducer::SimpleRoleResolver;
    use crate::canonical::records::{ObjectTbs, SignedObject};
    use crate::canonical::types::{CryptoProfileId, StorageClass};

    let client_key = KeyId::from_bytes([44u8; 32]);
    let signer = MockSigner { key_id: client_key };
    let verifier = MockVerifier;

    let seg_id = SegmentId::from_bytes([11u8; 32]);
    let ticket_id = TicketId::from_bytes([22u8; 16]);
    let stream_id = StreamId::from_bytes([33u8; 16]);
    let dek = DataEncryptionKey::new([0x42u8; 32]);

    let mut graph = EventGraph::new();
    let resolver = SimpleRoleResolver::new().with_client(client_key);

    let pack1_plain = crate::canonical::records::EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![LogicalEvent::TicketCreated {
            ticket_id,
            title: "Cold Start Base Ticket".into(),
            initial_epoch: 1000,
        }],
    };

    let pack1_signed = SignedObject {
        tbs: ObjectTbs {
            version: 1,
            network_id: NetworkId::from_bytes([0x11; 32]),
            segment_id: None,
            storage_class: StorageClass::StatePack,
            writer_key_id: client_key,
            writer_stream_id: stream_id,
            writer_seq: 1,
            prev_pack_id: None,
            crypto_profile: CryptoProfileId::FortiqPq1,
            envelope_set_digest: [0x22; 32],
            ciphertext_digest: [0x33; 32],
            ciphertext_len: 100,
        },
        signature: vec![0xaa; 64],
    };

    let p1 = graph
        .append_pack(
            VerifiedEventPack::new_unchecked(pack1_signed, pack1_plain).expect("verified pack 1"),
        )
        .expect("pack 1 append must succeed");

    let base_view =
        reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("base view must exist");
    assert_eq!(base_view.title, "Cold Start Base Ticket");

    let snapshot = TicketSnapshot::create(&base_view, 1000);

    // 1. Seal encrypted snapshot
    let enc_snapshot = EncryptedSnapshot::seal(&snapshot, seg_id, 1, &dek, &signer)
        .expect("sealing encrypted snapshot should succeed");

    assert_eq!(enc_snapshot.ticket_id, ticket_id);
    assert_eq!(enc_snapshot.author_key_id, client_key);
    assert_eq!(enc_snapshot.snapshot_seq, 1);
    assert_eq!(enc_snapshot.frontier_head_packs, vec![p1]);

    // 2. Tampered ciphertext fails decryption
    let mut tampered_ct = enc_snapshot.clone();
    tampered_ct.encrypted_payload[15] ^= 0xFF;
    assert!(tampered_ct.open(&dek, &verifier).is_err());

    // 3. Wrong key fails decryption
    let wrong_dek = DataEncryptionKey::new([0x99u8; 32]);
    assert!(enc_snapshot.open(&wrong_dek, &verifier).is_err());

    // 4. Clean open succeeds
    let opened_snapshot = enc_snapshot
        .open(&dek, &verifier)
        .expect("clean opening of snapshot must succeed");
    assert_eq!(opened_snapshot.ticket_id, ticket_id);
    assert_eq!(
        opened_snapshot.materialized_state.title,
        "Cold Start Base Ticket"
    );

    // 5. Fresh operator fast-forward catchup:
    // Append tail pack p2 (which adds a ChatMessage)
    let pack2_plain = crate::canonical::records::EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![LogicalEvent::ChatMessage {
            ticket_id,
            seq: 1,
            body: "Tail update message after snapshot".into(),
        }],
    };

    let pack2_signed = SignedObject {
        tbs: ObjectTbs {
            version: 1,
            network_id: NetworkId::from_bytes([0x11; 32]),
            segment_id: None,
            storage_class: StorageClass::StatePack,
            writer_key_id: client_key,
            writer_stream_id: stream_id,
            writer_seq: 2,
            prev_pack_id: Some(p1),
            crypto_profile: CryptoProfileId::FortiqPq1,
            envelope_set_digest: [0x22; 32],
            ciphertext_digest: [0x33; 32],
            ciphertext_len: 120,
        },
        signature: vec![0xbb; 64],
    };

    let p2 = graph
        .append_pack(
            VerifiedEventPack::new_unchecked(pack2_signed, pack2_plain).expect("verified pack 2"),
        )
        .expect("pack 2 append must succeed");

    // Reduce state starting from snapshot + graph tail
    let hydrated_view = reduce_ticket_from_snapshot(&opened_snapshot, &graph, &resolver);

    assert_eq!(hydrated_view.title, "Cold Start Base Ticket");
    assert_eq!(hydrated_view.messages.len(), 1);
    assert_eq!(
        hydrated_view.messages[0].body,
        "Tail update message after snapshot"
    );
    assert_eq!(hydrated_view.incorporated_packs, vec![p1, p2]);
}
