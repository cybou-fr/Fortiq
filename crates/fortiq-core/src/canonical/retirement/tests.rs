//! Tests for Phase 14 Legacy Retirement, Migration, and Cryptographic Authority.

use super::*;
use crate::canonical::control::capabilities;
use crate::canonical::events::graph::EventGraph;
use crate::canonical::portable::certificate::OperatorSessionCertificate;
use crate::canonical::records::LogicalEvent;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{AccessEpoch, EntityId, KeyId, NetworkId, OwnerId, StreamId};

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
fn test_legacy_ticket_event_migration() {
    let legacy_ticket = LegacyTicketRecord {
        id: "550e8400-e29b-41d4-a716-446655440000".to_string(),
        title: "Migrate legacy ticket".to_string(),
        description: "Moving from SQLite to EventGraph".to_string(),
        state_u8: 1, // InProgress
        client_peer_id: "12D3KooWClient".to_string(),
        operator_peer_id: "12D3KooWOperator".to_string(),
        remote_access_enabled: true,
        created_at: 100,
    };

    let legacy_messages = vec![
        LegacyChatMessageRecord {
            id: "msg-1".to_string(),
            ticket_id: legacy_ticket.id.clone(),
            sender: "client".to_string(),
            body: "Need help configuring network".to_string(),
            timestamp: 105,
        },
        LegacyChatMessageRecord {
            id: "msg-2".to_string(),
            ticket_id: legacy_ticket.id.clone(),
            sender: "operator".to_string(),
            body: "Checking now".to_string(),
            timestamp: 110,
        },
    ];

    let (ticket_id, events) =
        LegacyTicketMigrator::migrate_ticket_events(&legacy_ticket, &legacy_messages).unwrap();

    assert_eq!(events.len(), 5); // Created, msg1, msg2, StateChanged, AccessEpochGranted
    assert!(
        matches!(&events[0], LogicalEvent::TicketCreated { title, .. } if title == "Migrate legacy ticket")
    );
    assert!(
        matches!(&events[1], LogicalEvent::ChatMessage { seq: 1, body, .. } if body == "Need help configuring network")
    );
    assert!(
        matches!(&events[2], LogicalEvent::ChatMessage { seq: 2, body, .. } if body == "Checking now")
    );
    assert!(matches!(
        &events[3],
        LogicalEvent::TicketStateChanged { new_state: 1, .. }
    ));
    assert!(matches!(
        &events[4],
        LogicalEvent::AccessEpochGranted { .. }
    ));

    // Test migration into EventGraph and Snapshot generation
    let mut graph = EventGraph::new();
    let network_id = NetworkId::from_bytes([0x01; 32]);
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let writer_key = KeyId::from_bytes([0x02; 32]);

    let snapshot = LegacyTicketMigrator::migrate_into_graph(
        &mut graph,
        network_id,
        stream_id,
        writer_key,
        &legacy_ticket,
        &legacy_messages,
    )
    .expect("Migration into graph must succeed");

    assert_eq!(snapshot.ticket_id, ticket_id);
    assert_eq!(snapshot.materialized_state.title, "Migrate legacy ticket");
    assert_eq!(snapshot.materialized_state.messages.len(), 2);
    assert_eq!(
        snapshot.materialized_state.safety.access_epoch,
        events[4].access_epoch_granted().unwrap()
    );
}

trait LogicalEventExt {
    fn access_epoch_granted(&self) -> Option<AccessEpoch>;
}

impl LogicalEventExt for LogicalEvent {
    fn access_epoch_granted(&self) -> Option<AccessEpoch> {
        match self {
            LogicalEvent::AccessEpochGranted { access_epoch, .. } => {
                Some(AccessEpoch::from_bytes(*access_epoch))
            }
            _ => None,
        }
    }
}

#[test]
fn test_canonical_authority_resolution_and_rejection_of_static_roles() {
    // 1. Static peer ID check is rejected
    let err =
        CanonicalAuthorityResolver::reject_legacy_static_peer_id("12D3KooWLegacyStaticPeerId")
            .unwrap_err();
    assert!(matches!(err, AuthorityError::StaticPeerIdRejected(_)));

    // 2. Capabilities verification
    let held_caps = capabilities::WRITE_STATE | capabilities::OPEN_TICKET;
    assert!(CanonicalAuthorityResolver::verify_segment_capability(
        held_caps,
        capabilities::WRITE_STATE
    )
    .is_ok());
    assert!(CanonicalAuthorityResolver::verify_segment_capability(
        held_caps,
        capabilities::OPEN_TICKET
    )
    .is_ok());

    let admin_err =
        CanonicalAuthorityResolver::verify_segment_capability(held_caps, capabilities::ADMIN)
            .unwrap_err();
    assert!(matches!(
        admin_err,
        AuthorityError::InsufficientCapability { .. }
    ));

    // 3. Dynamic session certificate authorization
    let owner_signer = MockSigner {
        key_id: KeyId::from_bytes([0x77; 32]),
    };
    let owner_verifier = MockVerifier;
    let network_id = NetworkId::from_bytes([0x11; 32]);
    let owner_id = OwnerId::from_bytes([0x22; 32]);
    let operator_key_id = KeyId::from_bytes([0x33; 32]);
    let operator_entity = EntityId::from_bytes([0x88; 32]);

    let cert = OperatorSessionCertificate::issue(
        network_id,
        owner_id,
        operator_key_id,
        operator_entity,
        vec!["admin".to_string(), "shell".to_string()],
        1_000,
        2_000,
        &owner_signer,
    )
    .expect("Certificate issuance must succeed");

    assert_eq!(
        CanonicalAuthorityResolver::verify_operator_session(&cert, &owner_verifier, 1_500).unwrap(),
        operator_entity
    );

    // 4. Shell execution authorization with AccessEpoch
    let active_epoch = AccessEpoch::from_bytes([0xEE; 16]);
    let wrong_epoch = AccessEpoch::from_bytes([0x00; 16]);

    assert!(CanonicalAuthorityResolver::authorize_shell_execution(
        &cert,
        &owner_verifier,
        1_500,
        &active_epoch,
        &active_epoch,
    )
    .is_ok());

    let epoch_err = CanonicalAuthorityResolver::authorize_shell_execution(
        &cert,
        &owner_verifier,
        1_500,
        &wrong_epoch,
        &active_epoch,
    )
    .unwrap_err();
    assert_eq!(epoch_err, AuthorityError::AccessEpochInvalid);
}
