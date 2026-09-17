use crate::canonical::distribution::audit::{AuditSample, PeerAvailabilityTracker};
use crate::canonical::distribution::peer::StoragePeerCapability;
use crate::canonical::distribution::placement::{compute_rendezvous_score, PlacementEngine};
use crate::canonical::distribution::repair::RepairEngine;
use crate::canonical::distribution::streaming::{
    ShardStreamError, ShardStreamFrame, ShardStreamReceiver,
};
use crate::canonical::storage::erasure::{ErasureCoder, ReedSolomonCoder, Shard};
use crate::canonical::storage::policy::{evaluate_shard_health, ShardHealth};
use crate::canonical::types::{BlobId, EntityId, RsProfile};
use std::collections::HashSet;

fn dummy_peer(id_byte: u8, machine_byte: u8, weight: u32) -> StoragePeerCapability {
    StoragePeerCapability {
        node_id: EntityId::from_bytes([id_byte; 32]),
        peer_id: format!("12D3KooWPeer{}", id_byte),
        machine_id: [machine_byte; 16],
        site_id: Some("eu-west-par".into()),
        capacity_bytes: 100 * 1024 * 1024,
        max_shard_bytes: 4 * 1024 * 1024,
        free_budget_bytes: 50 * 1024 * 1024,
        weight,
    }
}

#[test]
fn test_rendezvous_deterministic_scoring() {
    let peer_a = dummy_peer(1, 10, 100);
    let peer_b = dummy_peer(2, 20, 100);
    let shard_hash = [0x55; 32];

    let score_a1 = compute_rendezvous_score(&shard_hash, &peer_a);
    let score_a2 = compute_rendezvous_score(&shard_hash, &peer_a);
    let score_b = compute_rendezvous_score(&shard_hash, &peer_b);

    assert_eq!(score_a1, score_a2, "Score must be completely deterministic");
    assert_ne!(
        score_a1, score_b,
        "Different peers should produce distinct scores"
    );
}

#[test]
fn test_failure_domain_machine_isolation() {
    let shard_hash = [0x77; 32];

    // 4 Candidate peers: Peer 1 & 2 share Machine 10; Peer 3 is on Machine 20; Peer 4 is on Machine 30
    let candidates = vec![
        dummy_peer(1, 10, 1000), // Machine 10
        dummy_peer(2, 10, 999),  // Machine 10 (same physical machine as peer 1)
        dummy_peer(3, 20, 500),  // Machine 20
        dummy_peer(4, 30, 400),  // Machine 30
    ];

    let empty_existing = HashSet::new();
    let selected =
        PlacementEngine::select_peers(&shard_hash, 1024, &candidates, 3, &empty_existing);

    assert_eq!(selected.len(), 3);
    // Peer 2 must be excluded because Peer 1 is already placed on Machine 10!
    let selected_ids: Vec<EntityId> = selected.iter().map(|p| p.node_id).collect();
    assert!(selected_ids.contains(&candidates[0].node_id));
    assert!(
        !selected_ids.contains(&candidates[1].node_id),
        "Duplicate machine_id must be rejected"
    );
    assert!(selected_ids.contains(&candidates[2].node_id));
    assert!(selected_ids.contains(&candidates[3].node_id));
}

#[test]
fn test_shard_streaming_protocol_success() {
    let local_node = EntityId::from_bytes([0x99; 32]);
    let mut receiver = ShardStreamReceiver::new(local_node, "12D3KooWReceiver");

    let blob_id = BlobId::from_bytes([0x12; 32]);
    let chunk1 = vec![0xaa; 32 * 1024]; // 32 KiB
    let chunk2 = vec![0xbb; 32 * 1024]; // 32 KiB
    let mut total_payload = Vec::new();
    total_payload.extend_from_slice(&chunk1);
    total_payload.extend_from_slice(&chunk2);

    let expected_hash = *blake3::hash(&total_payload).as_bytes();

    // 1. Send Open
    let open_frame = ShardStreamFrame::Open {
        blob_id,
        stripe_index: 0,
        shard_index: 1,
        expected_hash,
        total_bytes: total_payload.len() as u64,
    };
    assert_eq!(receiver.handle_frame(open_frame, 1000).unwrap(), None);

    // 2. Send Data chunks
    let data1 = ShardStreamFrame::Data {
        chunk_seq: 0,
        data: chunk1,
    };
    assert_eq!(receiver.handle_frame(data1, 1001).unwrap(), None);

    let data2 = ShardStreamFrame::Data {
        chunk_seq: 1,
        data: chunk2,
    };
    assert_eq!(receiver.handle_frame(data2, 1002).unwrap(), None);

    // 3. Send End
    let end_frame = ShardStreamFrame::End { total_chunks: 2 };
    let ack = receiver
        .handle_frame(end_frame, 1003)
        .unwrap()
        .expect("must yield ack");

    match ack {
        ShardStreamFrame::Ack {
            success,
            receipt,
            error,
        } => {
            assert!(success);
            assert!(error.is_none());
            let r = receipt.expect("must contain custody receipt");
            assert_eq!(r.shard_hash, expected_hash);
            assert_eq!(r.storing_peer, local_node);
            assert_eq!(r.acknowledged_bytes, 64 * 1024);
        }
        _ => panic!("unexpected frame"),
    }

    assert_eq!(receiver.into_buffer(), total_payload);
}

#[test]
fn test_shard_streaming_tamper_rejected() {
    let local_node = EntityId::from_bytes([0x99; 32]);
    let mut receiver = ShardStreamReceiver::new(local_node, "12D3KooWReceiver");

    let blob_id = BlobId::from_bytes([0x12; 32]);
    let chunk = vec![0x11; 1024];
    let expected_hash = [0x00; 32]; // deliberate mismatch

    let open_frame = ShardStreamFrame::Open {
        blob_id,
        stripe_index: 0,
        shard_index: 0,
        expected_hash,
        total_bytes: 1024,
    };
    receiver.handle_frame(open_frame, 1000).unwrap();

    let data_frame = ShardStreamFrame::Data {
        chunk_seq: 0,
        data: chunk,
    };
    receiver.handle_frame(data_frame, 1001).unwrap();

    let end_frame = ShardStreamFrame::End { total_chunks: 1 };
    let err = receiver
        .handle_frame(end_frame, 1002)
        .expect_err("tampered hash must fail");

    assert!(matches!(err, ShardStreamError::HashMismatch { .. }));
}

#[test]
fn test_peer_availability_scoring() {
    let mut tracker = PeerAvailabilityTracker::new();
    let peer = EntityId::from_bytes([0x44; 32]);

    assert!(tracker.is_peer_reliable(&peer));

    // 9 successful audits
    for _ in 0..9 {
        tracker.record_sample(AuditSample {
            peer,
            shard_hash: [0x11; 32],
            timestamp: 1000,
            success: true,
            latency_ms: 20,
        });
    }
    assert!(tracker.is_peer_reliable(&peer));

    // 2 failures -> 9 / 11 = 81% -> drops below 90%
    for _ in 0..2 {
        tracker.record_sample(AuditSample {
            peer,
            shard_hash: [0x11; 32],
            timestamp: 1010,
            success: false,
            latency_ms: 500,
        });
    }

    assert!(!tracker.is_peer_reliable(&peer));
    let score = tracker.get_score(&peer).unwrap();
    assert_eq!(score.total_audits, 11);
    assert_eq!(score.failed_audits, 2);
    assert_eq!(score.availability_percentage(), 81);
}

#[test]
fn test_decentralized_repair_stripe_regeneration() {
    let coder = ReedSolomonCoder::new();
    let profile = RsProfile {
        data_shards: 4,
        parity_shards: 2,
    };
    let payload = b"Distributed self-healing repair test payload across independent RS shards";

    let original_shards = coder.encode(payload, profile).expect("encode");
    assert_eq!(original_shards.len(), 6);

    // Simulate loss of shards 1 and 4
    let missing_indices = [1u8, 4u8];
    let mut surviving_shards: Vec<Option<Shard>> =
        original_shards.clone().into_iter().map(Some).collect();
    surviving_shards[1] = None;
    surviving_shards[4] = None;

    // Verify degraded state triggers repair requirement
    assert!(RepairEngine::needs_repair(4, profile));
    assert_eq!(evaluate_shard_health(4, profile), ShardHealth::Critical);

    // Execute repair
    let regenerated = RepairEngine::regenerate_missing_shards(
        &coder,
        &surviving_shards,
        payload.len(),
        profile,
        &missing_indices,
    )
    .expect("repair failed");

    assert_eq!(regenerated.len(), 2);

    // Verify regenerated shards match exact content and checksums of originals
    assert_eq!(regenerated[0].index, 1);
    assert_eq!(regenerated[0].checksum, original_shards[1].checksum);
    assert_eq!(regenerated[0].data, original_shards[1].data);

    assert_eq!(regenerated[1].index, 4);
    assert_eq!(regenerated[1].checksum, original_shards[4].checksum);
    assert_eq!(regenerated[1].data, original_shards[4].data);

    // Reintegrating restored shards returns stripe to Healthy status
    assert_eq!(evaluate_shard_health(6, profile), ShardHealth::Healthy);
}
