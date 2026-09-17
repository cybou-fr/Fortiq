use crate::canonical::storage::erasure::{ErasureCoder, ErasureError, ReedSolomonCoder, Shard};
use crate::canonical::storage::policy::{
    evaluate_shard_health, select_rs_profile, should_erasure_code, ShardHealth,
};
use crate::canonical::storage::stripe::{StripeDecoder, StripeEncoder};
use crate::canonical::types::{BlobId, NetworkId, RsProfile, SegmentId, StorageClass};

#[test]
fn test_reed_solomon_encode_and_reconstruct_all_shards() {
    let coder = ReedSolomonCoder::new();
    let profile = RsProfile {
        data_shards: 4,
        parity_shards: 2,
    };
    let payload = b"Cryptographic storage engine test payload using Reed-Solomon Cauchy codes!";

    let shards = coder.encode(payload, profile).expect("encode failed");
    assert_eq!(shards.len(), 6);

    let shards_opts: Vec<Option<Shard>> = shards.into_iter().map(Some).collect();
    let reconstructed = coder
        .reconstruct(&shards_opts, payload.len(), profile)
        .expect("reconstruct failed");
    assert_eq!(payload.as_slice(), reconstructed.as_slice());
}

#[test]
fn test_reed_solomon_reconstruct_with_any_two_erasures() {
    let coder = ReedSolomonCoder::new();
    let profile = RsProfile {
        data_shards: 4,
        parity_shards: 2,
    };
    let payload =
        b"Durability verification: Any m=2 shards can be completely lost without data loss.";

    let shards = coder.encode(payload, profile).expect("encode failed");
    assert_eq!(shards.len(), 6);

    // Test dropping combinations of 2 shards: (0, 1), (1, 4), (4, 5)
    let loss_combinations = [(0, 1), (1, 4), (4, 5), (2, 3)];

    for (drop_a, drop_b) in loss_combinations {
        let mut degraded_shards: Vec<Option<Shard>> =
            shards.clone().into_iter().map(Some).collect();
        degraded_shards[drop_a] = None;
        degraded_shards[drop_b] = None;

        let reconstructed = coder
            .reconstruct(&degraded_shards, payload.len(), profile)
            .unwrap_or_else(|_| {
                panic!("failed to reconstruct with drops ({}, {})", drop_a, drop_b)
            });

        assert_eq!(
            payload.as_slice(),
            reconstructed.as_slice(),
            "reconstructed payload mismatch for drops ({}, {})",
            drop_a,
            drop_b
        );
    }
}

#[test]
fn test_reed_solomon_corrupt_shard_detection_and_recovery() {
    let coder = ReedSolomonCoder::new();
    let profile = RsProfile {
        data_shards: 2,
        parity_shards: 1,
    };
    let payload = b"Corrupt shard rejection and parity fallback test";

    let mut shards = coder.encode(payload, profile).expect("encode failed");
    assert_eq!(shards.len(), 3);

    // Verify intact shard checksum passes
    assert!(shards[0].verify_checksum());

    // Tamper 1 byte in shard 0
    shards[0].data[0] ^= 0xff;

    // Checksum verification must fail!
    assert!(!shards[0].verify_checksum());

    // Reconstruct with the corrupted shard included: the coder detects the corrupt checksum,
    // automatically treats it as an erasure, and reconstructs from surviving shards!
    let shards_opts: Vec<Option<Shard>> = shards.into_iter().map(Some).collect();
    let recovered = coder
        .reconstruct(&shards_opts, payload.len(), profile)
        .expect("must recover using parity shard");

    assert_eq!(payload.as_slice(), recovered.as_slice());
}

#[test]
fn test_reed_solomon_insufficient_shards_error() {
    let coder = ReedSolomonCoder::new();
    let profile = RsProfile {
        data_shards: 4,
        parity_shards: 2,
    };
    let payload = b"Loss exceeding parity capacity";

    let shards = coder.encode(payload, profile).expect("encode failed");

    // Drop 3 shards (exceeding parity capacity of 2)
    let mut shards_opts: Vec<Option<Shard>> = shards.into_iter().map(Some).collect();
    shards_opts[0] = None;
    shards_opts[1] = None;
    shards_opts[2] = None;

    let err = coder
        .reconstruct(&shards_opts, payload.len(), profile)
        .expect_err("must fail when available < k");

    assert_eq!(
        err,
        ErasureError::NotEnoughShards {
            needed: 4,
            available: 3
        }
    );
}

#[test]
fn test_stripe_encoder_and_decoder_multi_stripe() {
    let encoder = StripeEncoder::with_default();
    let decoder = StripeDecoder::with_default();

    let network_id = NetworkId::from_bytes([0x01; 32]);
    let segment_id = SegmentId::from_bytes([0x02; 32]);
    let blob_id = BlobId::from_bytes([0x03; 32]);

    // 100 KiB test payload
    let payload: Vec<u8> = (0..100 * 1024).map(|i| (i % 251) as u8).collect();
    let stripe_size = 32 * 1024; // 32 KiB stripe -> 4 stripes
    let profile = RsProfile {
        data_shards: 4,
        parity_shards: 2,
    };

    let (manifest, stripes_shards) = encoder
        .encode_blob(
            network_id,
            segment_id,
            blob_id,
            &payload,
            profile,
            stripe_size,
        )
        .expect("encode blob failed");

    assert_eq!(manifest.stripes.len(), 4);
    assert_eq!(manifest.ciphertext_len, 100 * 1024);

    // Drop 1 random shard in each stripe
    let mut degraded_stripes: Vec<Vec<Option<Shard>>> = Vec::new();
    for (idx, stripe) in stripes_shards.into_iter().enumerate() {
        let mut opts: Vec<Option<Shard>> = stripe.into_iter().map(Some).collect();
        let drop_idx = idx % opts.len();
        opts[drop_idx] = None;
        degraded_stripes.push(opts);
    }

    // Decode blob under multi-stripe degradations
    let decoded = decoder
        .decode_blob(&manifest, &degraded_stripes)
        .expect("decode blob failed");

    assert_eq!(payload, decoded);
}

#[test]
fn test_storage_policy_thresholds() {
    // Control: never erasure coded
    assert!(!should_erasure_code(StorageClass::Control, 1024));
    assert!(!should_erasure_code(StorageClass::Control, 1024 * 1024));

    // StatePack: replicated under 64 KiB, RS at or above 64 KiB
    assert!(!should_erasure_code(StorageClass::StatePack, 64 * 1024 - 1));
    assert!(should_erasure_code(StorageClass::StatePack, 64 * 1024));
    assert!(should_erasure_code(StorageClass::StatePack, 128 * 1024));

    // Blob: always erasure coded
    assert!(should_erasure_code(StorageClass::Blob, 10));
    assert!(should_erasure_code(StorageClass::Blob, 10 * 1024 * 1024));
}

#[test]
fn test_adaptive_profile_selection_and_health() {
    assert_eq!(
        select_rs_profile(1),
        RsProfile {
            data_shards: 1,
            parity_shards: 0
        }
    );
    assert_eq!(
        select_rs_profile(2),
        RsProfile {
            data_shards: 1,
            parity_shards: 1
        }
    );
    assert_eq!(
        select_rs_profile(4),
        RsProfile {
            data_shards: 2,
            parity_shards: 1
        }
    );
    assert_eq!(
        select_rs_profile(7),
        RsProfile {
            data_shards: 4,
            parity_shards: 2
        }
    );
    assert_eq!(
        select_rs_profile(12),
        RsProfile {
            data_shards: 6,
            parity_shards: 3
        }
    );

    let profile = RsProfile {
        data_shards: 4,
        parity_shards: 2,
    };
    // 6 reachable -> Healthy
    assert_eq!(evaluate_shard_health(6, profile), ShardHealth::Healthy);
    // 5 reachable -> Degraded
    assert_eq!(evaluate_shard_health(5, profile), ShardHealth::Degraded);
    // 4 reachable -> Critical
    assert_eq!(evaluate_shard_health(4, profile), ShardHealth::Critical);
    // 3 reachable -> Lost
    assert_eq!(evaluate_shard_health(3, profile), ShardHealth::Lost);
}
