#[cfg(test)]
mod tests {
    use crate::canonical::codec::{from_canonical_cbor, to_canonical_cbor, DecoderLimits};
    use crate::canonical::control::{
        capabilities, derive_owner_id, ControlError, DeviceBinding, EnrollmentCertificate, Genesis,
        GenesisTbs, JoinInvitation, RevocationList, SegmentDescriptor, GENESIS_SIG_DOMAIN,
    };
    use crate::canonical::control_store::ControlStore;
    use crate::canonical::signing::{Ed25519Signer, Signer};
    use crate::canonical::types::{CryptoProfileId, EntityId, KeyId, NetworkId, SegmentId};

    fn sample_genesis() -> Genesis {
        let signer = Ed25519Signer::from_seed([0x42; 32]);
        let owner_pk = signer.public_key().to_vec();
        let owner_id = derive_owner_id(&owner_pk);
        let tbs = GenesisTbs {
            version: 1,
            network_id: NetworkId::from_bytes([0x77; 32]),
            owner_id,
            owner_root_signing_public_key: owner_pk,
            recovery_public_key: None,
            initial_crypto_profile: CryptoProfileId::FortiqClassicalDev1,
            initial_policy_hash: [0x88; 32],
            created_at: 1726570000,
        };

        let mut payload = GENESIS_SIG_DOMAIN.to_vec();
        payload.extend_from_slice(&to_canonical_cbor(&tbs).unwrap());
        let signature = signer.sign(&payload).unwrap();
        Genesis { tbs, signature }
    }

    #[test]
    fn test_genesis_owner_and_genesis_id_derivation() {
        let genesis = sample_genesis();
        genesis.verify().expect("valid Genesis signature");
        let genesis_id = genesis.genesis_id().expect("genesis id failed");
        assert_ne!(genesis_id, [0u8; 32]);

        // Recomputing must yield identical genesis ID
        let genesis_id_2 = genesis.genesis_id().expect("genesis id failed");
        assert_eq!(genesis_id, genesis_id_2);

        // Check OwnerId derivation
        let owner_pk = &genesis.tbs.owner_root_signing_public_key;
        let owner_id = derive_owner_id(owner_pk);
        assert_eq!(genesis.tbs.owner_id, owner_id);
    }

    #[test]
    fn test_genesis_verification_rejects_tampering_and_owner_mismatch() {
        let mut tampered = sample_genesis();
        tampered.tbs.created_at += 1;
        assert!(tampered.verify().is_err());

        let mut mismatched = sample_genesis();
        mismatched.tbs.owner_id = crate::canonical::types::OwnerId::from_bytes([0xff; 32]);
        assert!(matches!(
            mismatched.verify(),
            Err(ControlError::OwnerIdentityMismatch)
        ));
    }

    #[test]
    fn test_genesis_cbor_roundtrip() {
        let genesis = sample_genesis();
        let bytes = to_canonical_cbor(&genesis).expect("genesis cbor serialization failed");
        let decoded: Genesis =
            from_canonical_cbor(&bytes, DecoderLimits::CONTROL).expect("genesis decoding failed");
        assert_eq!(genesis, decoded);
    }

    #[test]
    fn test_join_invitation_cbor_roundtrip() {
        let genesis = sample_genesis();
        let genesis_id = genesis.genesis_id().unwrap();

        let invitation = JoinInvitation {
            network_id: genesis.tbs.network_id,
            genesis_id,
            bootstrap_peers: vec!["/ip4/127.0.0.1/udp/4001/quic-v1".to_string()],
            join_nonce: [0x33; 16],
            expires_at: 1726580000,
            capability_template: capabilities::WRITE_STATE | capabilities::OPEN_TICKET,
            owner_signature: vec![0xaa; 64],
        };

        let bytes = to_canonical_cbor(&invitation).expect("invitation serialization failed");
        let decoded: JoinInvitation = from_canonical_cbor(&bytes, DecoderLimits::CONTROL)
            .expect("invitation decoding failed");
        assert_eq!(invitation, decoded);
    }

    #[test]
    fn test_segment_descriptor_roundtrip() {
        let genesis = sample_genesis();

        let descriptor = SegmentDescriptor {
            version: 1,
            network_id: genesis.tbs.network_id,
            segment_id: SegmentId::from_bytes([0x55; 32]),
            owner_id: genesis.tbs.owner_id,
            operator_segment_hpke_public_key: vec![0x11; 32],
            client_entity_id: EntityId::from_bytes([0x22; 32]),
            client_signing_public_key: vec![0x33; 32],
            client_hpke_public_key: vec![0x44; 32],
            key_epoch: 1,
            quota_profile: 1,
            created_at: 1726570000,
            owner_signature: vec![0x55; 64],
            client_acceptance_signature: vec![0x66; 64],
        };

        let bytes = to_canonical_cbor(&descriptor).expect("descriptor serialization failed");
        let decoded: SegmentDescriptor = from_canonical_cbor(&bytes, DecoderLimits::CONTROL)
            .expect("descriptor decoding failed");
        assert_eq!(descriptor, decoded);
    }

    #[test]
    fn test_control_store_operations_and_revocations() {
        let genesis = sample_genesis();
        let net_id = genesis.tbs.network_id;
        let store = ControlStore::new(genesis).expect("store init failed");

        let seg_id = SegmentId::from_bytes([0x55; 32]);
        let entity_id = EntityId::from_bytes([0x22; 32]);
        let key_id = KeyId::from_bytes([0x88; 32]);

        // Insert Enrollment
        let cert = EnrollmentCertificate {
            network_id: net_id,
            entity_id,
            signing_public_key: vec![0x33; 32],
            capabilities: capabilities::WRITE_STATE,
            valid_until: 1726600000,
            owner_signature: vec![0x12; 64],
        };
        store
            .insert_enrollment(cert.clone())
            .expect("enrollment failed");
        assert_eq!(store.get_enrollment(&entity_id), Some(cert));

        // Insert DeviceBinding
        let binding = DeviceBinding {
            network_id: net_id,
            entity_id,
            peer_id: "12D3KooWTestPeerId".to_string(),
            valid_until: 1726600000,
            entity_signature: vec![0x34; 64],
        };
        store
            .insert_device_binding(binding.clone())
            .expect("binding failed");
        assert_eq!(store.get_device_binding(&entity_id), Some(binding));

        // Mismatched Network rejection
        let alien_descriptor = SegmentDescriptor {
            version: 1,
            network_id: NetworkId::from_bytes([0x99; 32]),
            segment_id: seg_id,
            owner_id: store.genesis().tbs.owner_id,
            operator_segment_hpke_public_key: vec![0x11; 32],
            client_entity_id: entity_id,
            client_signing_public_key: vec![0x33; 32],
            client_hpke_public_key: vec![0x44; 32],
            key_epoch: 1,
            quota_profile: 1,
            created_at: 1726570000,
            owner_signature: vec![0x55; 64],
            client_acceptance_signature: vec![0x66; 64],
        };
        assert!(matches!(
            store.insert_segment_descriptor(alien_descriptor),
            Err(ControlError::MismatchedNetwork(_, _))
        ));

        // Apply Revocation List
        assert!(!store.is_entity_revoked(&entity_id));
        assert!(!store.is_key_revoked(&key_id));

        let rev = RevocationList {
            network_id: net_id,
            revocation_epoch: 1,
            revoked_entities: vec![entity_id],
            revoked_keys: vec![key_id],
            reason: "Key compromised".to_string(),
            created_at: 1726571000,
            owner_signature: vec![0x77; 64],
        };
        store.apply_revocations(rev).expect("revocation failed");
        assert_eq!(store.current_revocation_epoch(), 1);
        assert!(store.is_entity_revoked(&entity_id));
        assert!(store.is_key_revoked(&key_id));

        // Stale revocation epoch must be rejected (monotonicity invariant)
        let stale_rev = RevocationList {
            network_id: net_id,
            revocation_epoch: 1,
            revoked_entities: vec![],
            revoked_keys: vec![],
            reason: "Stale update".to_string(),
            created_at: 1726572000,
            owner_signature: vec![0x77; 64],
        };
        assert!(matches!(
            store.apply_revocations(stale_rev),
            Err(ControlError::StaleRevocationEpoch(1, 1))
        ));

        // Enrolling a revoked entity must now be rejected
        let cert2 = EnrollmentCertificate {
            network_id: net_id,
            entity_id,
            signing_public_key: vec![0x33; 32],
            capabilities: capabilities::WRITE_STATE,
            valid_until: 1726700000,
            owner_signature: vec![0x12; 64],
        };
        assert!(matches!(
            store.insert_enrollment(cert2),
            Err(ControlError::EntityRevoked(e)) if e == entity_id
        ));
    }
}
