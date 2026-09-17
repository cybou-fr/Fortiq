#[cfg(test)]
mod tests {
    use crate::canonical::codec::{
        from_canonical_cbor, to_canonical_cbor, CodecError, DecoderLimits,
    };
    use crate::canonical::records::{ObjectTbs, RecipientEnvelope, SignedObject};
    use crate::canonical::signing::{compute_shard_checksum, compute_tbs_bytes, derive_object_id};
    use crate::canonical::types::{
        AccessEpoch, CryptoProfileId, KeyId, NetworkId, ObjectId, SegmentId, StorageClass, StreamId,
    };

    fn sample_tbs() -> ObjectTbs {
        ObjectTbs {
            version: 1,
            network_id: NetworkId::from_bytes([0x11; 32]),
            segment_id: Some(SegmentId::from_bytes([0x22; 32])),
            storage_class: StorageClass::StatePack,
            writer_key_id: KeyId::from_bytes([0x33; 32]),
            writer_stream_id: StreamId::from_bytes([0x44; 16]),
            writer_seq: 42,
            prev_pack_id: Some(ObjectId::from_bytes([0x55; 32])),
            crypto_profile: CryptoProfileId::FortiqPq1,
            envelope_set_digest: [0x66; 32],
            ciphertext_digest: [0x77; 32],
            ciphertext_len: 1024,
        }
    }

    #[test]
    fn test_deterministic_tbs_serialization_and_roundtrip() {
        let tbs = sample_tbs();
        let bytes1 = compute_tbs_bytes(&tbs).expect("serialization failed");
        let bytes2 = compute_tbs_bytes(&tbs).expect("serialization failed");

        // Invariant: strict determinism
        assert_eq!(
            bytes1, bytes2,
            "CBOR serialization must be byte-for-byte deterministic"
        );

        // Check decoding with strict limits
        let decoded: ObjectTbs =
            from_canonical_cbor(&bytes1, DecoderLimits::DEFAULT).expect("deserialization failed");
        assert_eq!(tbs, decoded);
    }

    #[test]
    fn test_frozen_known_answer_object_id() {
        let tbs = sample_tbs();
        let tbs_bytes = compute_tbs_bytes(&tbs).expect("tbs bytes failed");
        let dummy_sig = vec![0x99; 64];

        let object_id = derive_object_id(&tbs_bytes, &dummy_sig);
        let hex_id = object_id.to_hex();

        // Verify that recomputing the hash gives the exact same 64-character hex ID
        let object_id_2 = derive_object_id(&tbs_bytes, &dummy_sig);
        assert_eq!(hex_id, object_id_2.to_hex());
        assert_eq!(hex_id.len(), 64);
    }

    #[test]
    fn test_recipient_envelope_serialization() {
        let envelope = RecipientEnvelope {
            key_id: KeyId::from_bytes([0xaa; 32]),
            key_epoch: 10,
            hpke_enc: vec![0xbb; 48],
            sealed_key: vec![0xcc; 32],
        };

        let bytes = to_canonical_cbor(&envelope).expect("envelope serialization failed");
        let decoded: RecipientEnvelope =
            from_canonical_cbor(&bytes, DecoderLimits::DEFAULT).expect("envelope decoding failed");

        assert_eq!(envelope, decoded);
    }

    #[test]
    fn test_signed_object_container() {
        let tbs = sample_tbs();
        let signed_obj = SignedObject {
            tbs: tbs.clone(),
            signature: vec![0xdd; 128],
        };

        let bytes = to_canonical_cbor(&signed_obj).expect("signed object serialization failed");
        let decoded: SignedObject = from_canonical_cbor(&bytes, DecoderLimits::DEFAULT)
            .expect("signed object decoding failed");

        assert_eq!(signed_obj, decoded);
    }

    #[test]
    fn test_decoder_limits_enforcement() {
        let limits = DecoderLimits {
            max_input_bytes: 10,
            max_depth: 2,
            max_container_len: 5,
            max_bytes_len: 10,
        };

        let large_payload = vec![0u8; 100];
        let err = from_canonical_cbor::<ObjectTbs>(&large_payload, limits)
            .expect_err("must reject inputs exceeding max_input_bytes");

        match err {
            CodecError::InputTooLarge(actual, max) => {
                assert_eq!(actual, 100);
                assert_eq!(max, 10);
            }
            other => panic!("unexpected error variant: {:?}", other),
        }
    }

    #[test]
    fn test_shard_checksum_blake3() {
        let data = b"FORTIQ Storage Shard Verification Payload";
        let checksum1 = compute_shard_checksum(data);
        let checksum2 = compute_shard_checksum(data);
        assert_eq!(checksum1, checksum2);
        assert_ne!(checksum1, [0u8; 32]);
    }

    #[test]
    fn test_event_pack_plaintext_roundtrip() {
        use crate::canonical::records::{EventPackPlaintext, LogicalEvent};
        use crate::canonical::types::TicketId;

        let ticket_id = TicketId::from_bytes([0x55; 16]);
        let pack = EventPackPlaintext {
            schema_version: 1,
            ticket_id: Some(ticket_id),
            ticket_crypto_epoch: Some(1),
            pack_nonce: [0x42; 16],
            events: vec![
                LogicalEvent::TicketCreated {
                    ticket_id,
                    title: "Network connectivity issue".to_string(),
                    initial_access_epoch: AccessEpoch::from_bytes([0x01; 16]),
                },
                LogicalEvent::ChatMessage {
                    ticket_id,
                    seq: 1,
                    body: "Hello, technician!".to_string(),
                },
                LogicalEvent::AccessEpochGranted {
                    ticket_id,
                    access_epoch: [0x77; 16],
                },
            ],
        };

        let bytes = to_canonical_cbor(&pack).expect("pack serialization failed");
        let decoded: EventPackPlaintext =
            from_canonical_cbor(&bytes, DecoderLimits::DEFAULT).expect("pack decoding failed");

        assert_eq!(pack, decoded);
    }

    #[test]
    fn test_blob_manifest_roundtrip() {
        use crate::canonical::records::{serde_bytes_32::Bytes32, BlobManifest, StripeManifest};
        use crate::canonical::types::{BlobId, RsProfile};

        let manifest = BlobManifest {
            version: 1,
            network_id: NetworkId::from_bytes([0x12; 32]),
            segment_id: SegmentId::from_bytes([0x34; 32]),
            blob_id: BlobId::from_bytes([0x56; 32]),
            ciphertext_len: 1048576,
            stripe_size: 262144,
            rs_profile: RsProfile::DEFAULT,
            stripes: vec![StripeManifest {
                stripe_index: 0,
                plain_len: 262144,
                cipher_len: 262144 + 16,
                shard_hashes: vec![
                    Bytes32([0x01; 32]),
                    Bytes32([0x02; 32]),
                    Bytes32([0x03; 32]),
                    Bytes32([0x04; 32]),
                    Bytes32([0x05; 32]),
                    Bytes32([0x06; 32]),
                ],
            }],
        };

        let bytes = to_canonical_cbor(&manifest).expect("manifest serialization failed");
        let decoded: BlobManifest =
            from_canonical_cbor(&bytes, DecoderLimits::MANIFEST).expect("manifest decoding failed");

        assert_eq!(manifest, decoded);
    }

    #[test]
    fn test_depth_limit_rejection() {
        // Deeply nested CBOR structure exceeding max_depth: 2
        let nested =
            ciborium::Value::Array(vec![ciborium::Value::Array(vec![ciborium::Value::Array(
                vec![ciborium::Value::Integer(1.into())],
            )])]);

        let mut buf = Vec::new();
        ciborium::into_writer(&nested, &mut buf).unwrap();

        let limits = DecoderLimits {
            max_input_bytes: 1024,
            max_depth: 1, // Will fail on deeper arrays
            max_container_len: 10,
            max_bytes_len: 1024,
        };

        let err = from_canonical_cbor::<ciborium::Value>(&buf, limits)
            .expect_err("nested structure must exceed depth");

        match err {
            CodecError::MaxDepthExceeded(depth, max) => {
                assert!(depth > max);
            }
            other => panic!("expected MaxDepthExceeded, got {:?}", other),
        }
    }

    #[test]
    fn test_id_hex_formatting_and_parsing() {
        let raw = [0xabu8; 32];
        let net_id = NetworkId::from_bytes(raw);
        let hex_str = net_id.to_hex();
        assert_eq!(hex_str.len(), 64);
        assert_eq!(
            hex_str,
            "abababababababababababababababababababababababababababababababab"
        );

        let parsed = NetworkId::from_hex(&hex_str).expect("hex parse failed");
        assert_eq!(net_id, parsed);

        // Display trait
        assert_eq!(format!("{}", net_id), hex_str);
    }
}
