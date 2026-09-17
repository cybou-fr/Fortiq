use crate::canonical::events::batcher::{BatchPolicy, EventPackBatcher, FlushDecision};
use crate::canonical::events::graph::{EventGraph, VerifiedEventPack};
use crate::canonical::events::reducer::{reduce_ticket_with_resolver, SimpleRoleResolver};
use crate::canonical::events::safety::{TicketLifecycle, TicketSafetyState};
use crate::canonical::events::stream::{StreamCursor, StreamError};
use crate::canonical::events::tombstone::{CanonicalHeadSet, Tombstone};
use crate::canonical::records::{EventPackPlaintext, LogicalEvent, ObjectTbs, SignedObject};
use crate::canonical::types::{
    AccessEpoch, BlobId, CryptoProfileId, KeyId, NetworkId, ObjectId, StorageClass, StreamId,
    TicketId,
};

fn dummy_signed_object(
    writer_key_id: KeyId,
    stream_id: StreamId,
    seq: u64,
    prev_pack_id: Option<ObjectId>,
    ciphertext_len: u64,
) -> SignedObject {
    let tbs = ObjectTbs {
        version: 1,
        network_id: NetworkId::from_bytes([0x11; 32]),
        segment_id: None,
        storage_class: StorageClass::StatePack,
        writer_key_id,
        writer_stream_id: stream_id,
        writer_seq: seq,
        prev_pack_id,
        crypto_profile: CryptoProfileId::FortiqPq1,
        envelope_set_digest: [0x22; 32],
        ciphertext_digest: [0x33; 32],
        ciphertext_len,
    };
    SignedObject {
        tbs,
        signature: vec![0xaa; 64],
    }
}

fn append_pack_unchecked(
    graph: &mut EventGraph,
    signed_obj: SignedObject,
    plaintext: EventPackPlaintext,
) -> Result<ObjectId, crate::canonical::events::graph::EventGraphError> {
    graph.append_pack(VerifiedEventPack::new_unchecked(signed_obj, plaintext)?)
}

#[test]
fn test_writer_stream_append_and_fork_detection() {
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let mut cursor = StreamCursor::new(stream_id);

    let pack1 = ObjectId::from_bytes([0x10; 32]);
    let pack2 = ObjectId::from_bytes([0x20; 32]);
    let pack3 = ObjectId::from_bytes([0x30; 32]);

    // Seq 1 with no prev -> OK
    let entry1 = cursor
        .accept_append(1, None, pack1)
        .expect("seq 1 must succeed");
    assert_eq!(entry1.seq, 1);
    assert_eq!(entry1.prev_pack_id, None);
    assert_eq!(entry1.pack_id, pack1);

    // Seq 2 with prev pack1 -> OK
    let entry2 = cursor
        .accept_append(2, Some(pack1), pack2)
        .expect("seq 2 must succeed");
    assert_eq!(entry2.seq, 2);
    assert_eq!(entry2.prev_pack_id, Some(pack1));
    assert_eq!(entry2.pack_id, pack2);

    // Fork: competing successor for seq 2 -> ForkDetected
    let fork_err = cursor
        .accept_append(2, Some(pack1), pack3)
        .expect_err("competing seq 2 must be rejected as a fork");
    assert!(matches!(fork_err, StreamError::ForkDetected { .. }));

    // Gap: jumping to seq 5 -> SequenceGap
    let gap_err = cursor
        .accept_append(5, Some(pack2), pack3)
        .expect_err("seq gap must be rejected");
    assert!(matches!(gap_err, StreamError::SequenceGap { .. }));

    // Wrong prev: seq 3 with wrong prev -> ForkDetected
    let wrong_prev_err = cursor
        .accept_append(3, Some(pack1), pack3)
        .expect_err("wrong prev must be rejected");
    assert!(matches!(wrong_prev_err, StreamError::ForkDetected { .. }));

    // Correct seq 3 -> OK
    let entry3 = cursor
        .accept_append(3, Some(pack2), pack3)
        .expect("seq 3 must succeed");
    assert_eq!(entry3.seq, 3);
    assert_eq!(entry3.prev_pack_id, Some(pack2));
}

#[test]
fn test_event_pack_batcher_immediate_safety_flush() {
    let ticket_id = TicketId::from_bytes([0x77; 16]);
    let mut batcher = EventPackBatcher::new(Some(ticket_id), Some(1));

    // Regular chat messages buffer normally
    let chat1 = LogicalEvent::ChatMessage {
        ticket_id,
        seq: 1,
        body: "Hello operator".into(),
    };
    assert_eq!(batcher.push(chat1), FlushDecision::Buffered);
    assert_eq!(batcher.len(), 1);

    let chat2 = LogicalEvent::ChatMessage {
        ticket_id,
        seq: 2,
        body: "Here is more info".into(),
    };
    assert_eq!(batcher.push(chat2), FlushDecision::Buffered);
    assert_eq!(batcher.len(), 2);

    // Immediate safety event: AccessEpochRevoked must flush immediately!
    let revoke = LogicalEvent::AccessEpochRevoked {
        ticket_id,
        access_epoch: [0x55; 16],
    };
    assert_eq!(batcher.push(revoke), FlushDecision::FlushImmediately);

    // Flush creates valid pack
    let pack = batcher.flush().expect("must flush accumulated events");
    assert_eq!(pack.events.len(), 3);
    assert_ne!(pack.pack_nonce, [0u8; 16]);
    assert!(batcher.is_empty());
}

#[test]
fn test_event_pack_batcher_max_events_threshold() {
    let policy = BatchPolicy {
        max_events: 3,
        max_delay_ms: 1000,
        target_plaintext_bytes: 64 * 1024,
        hard_max_bytes: 256 * 1024,
    };
    let ticket_id = TicketId::from_bytes([0x77; 16]);
    let mut batcher = EventPackBatcher::with_policy(Some(ticket_id), Some(1), policy);

    let chat = |i| LogicalEvent::ChatMessage {
        ticket_id,
        seq: i,
        body: format!("msg {}", i),
    };

    assert_eq!(batcher.push(chat(1)), FlushDecision::Buffered);
    assert_eq!(batcher.push(chat(2)), FlushDecision::Buffered);
    // 3rd reaches max_events threshold -> FlushImmediately
    assert_eq!(batcher.push(chat(3)), FlushDecision::FlushImmediately);
}

#[test]
fn test_client_access_epoch_safety_invariants() {
    let ticket_id = TicketId::from_bytes([0x99; 16]);
    let initial_epoch = AccessEpoch::from_bytes([0x11; 16]);
    let mut safety = TicketSafetyState::new_client_open(ticket_id, initial_epoch);

    // Invariant 8: Client-owned initial valid state
    assert!(safety.permits_shell());
    assert_eq!(safety.lifecycle, TicketLifecycle::Open);

    // Operator moves to InProgress: still valid
    safety.set_in_progress_by_operator();
    assert!(safety.permits_shell());
    assert_eq!(safety.lifecycle, TicketLifecycle::InProgress);

    // Invariant 9: Client immediate revocation
    safety.revoke_by_client();
    assert!(!safety.permits_shell());
    assert!(!safety.access_valid);

    // Operator CANNOT restore access
    safety.set_in_progress_by_operator();
    assert!(
        !safety.permits_shell(),
        "Operator MUST NOT restore shell permission"
    );

    // Client reopens ticket with NEW epoch
    let new_epoch = AccessEpoch::from_bytes([0x22; 16]);
    safety.reopen_by_client(new_epoch);
    assert!(
        safety.permits_shell(),
        "Client reopen with new epoch must grant access"
    );
    assert_eq!(safety.access_epoch, new_epoch);
    assert_eq!(safety.lifecycle, TicketLifecycle::Open);
}

#[test]
fn test_reducer_full_ticket_reconstruction_and_tombstone() {
    let mut graph = EventGraph::new();
    let ticket_id = TicketId::from_bytes([0x42; 16]);
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let operator_key = KeyId::from_bytes([0x02; 32]);
    let resolver = SimpleRoleResolver::new()
        .with_client(client_key)
        .with_operator(operator_key);

    // Pack 1: TicketCreated by Client
    let initial_epoch = 12345u64;
    let pack1_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![LogicalEvent::TicketCreated {
            ticket_id,
            title: "Crashing Wi-Fi adapter".into(),
            initial_epoch,
        }],
    };
    let pack1_signed = dummy_signed_object(client_key, stream_id, 1, None, 100);
    let pack1_id =
        append_pack_unchecked(&mut graph, pack1_signed, pack1_plain).expect("pack 1 append failed");

    // Pack 2: ChatMessage + FileAttached
    let blob_id = BlobId::from_bytes([0xbb; 32]);
    let pack2_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![
            LogicalEvent::ChatMessage {
                ticket_id,
                seq: 1,
                body: "Attaching crash dump log".into(),
            },
            LogicalEvent::FileAttached {
                ticket_id,
                blob_id,
                filename: "crash.log".into(),
                size_bytes: 4096,
            },
        ],
    };
    let pack2_signed = dummy_signed_object(client_key, stream_id, 2, Some(pack1_id), 200);
    let pack2_id =
        append_pack_unchecked(&mut graph, pack2_signed, pack2_plain).expect("pack 2 append failed");

    // Reduce: both messages and attachments present
    let view =
        reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("ticket view must exist");
    assert_eq!(view.title, "Crashing Wi-Fi adapter");
    assert_eq!(view.messages.len(), 1);
    assert_eq!(view.attachments.len(), 1);
    assert_eq!(view.attachments[0].filename, "crash.log");
    assert!(view.safety.permits_shell());

    // Logical Deletion via Tombstone: delete pack 2
    let tombstone = Tombstone::new(
        pack2_id,
        crate::canonical::types::EntityId::from_bytes([0x99; 32]),
        "User retracted log",
        1000,
    );
    graph.add_tombstone(tombstone);

    // Reduce again: pack 2 is logically excluded!
    let view_after_tombstone =
        reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("ticket view must exist");
    assert_eq!(view_after_tombstone.messages.len(), 0);
    assert_eq!(view_after_tombstone.attachments.len(), 0);
    assert!(view_after_tombstone.safety.permits_shell());
}

#[test]
fn test_canonical_head_set_cannot_override_client_safety_revocation() {
    let mut graph = EventGraph::new();
    let ticket_id = TicketId::from_bytes([0x55; 16]);
    let stream_client = StreamId::from_bytes([0x01; 16]);
    let stream_op = StreamId::from_bytes([0x02; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let operator_key = KeyId::from_bytes([0x02; 32]);
    let resolver = SimpleRoleResolver::new()
        .with_client(client_key)
        .with_operator(operator_key);

    // Pack 1: Client creates ticket
    let pack1_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![LogicalEvent::TicketCreated {
            ticket_id,
            title: "Security diagnosis".into(),
            initial_epoch: 111,
        }],
    };
    let pack1_signed = dummy_signed_object(client_key, stream_client, 1, None, 100);
    let pack1_id = append_pack_unchecked(&mut graph, pack1_signed, pack1_plain).expect("pack 1");

    // Pack 2: Client revokes access immediately
    let mut epoch_bytes = [0u8; 16];
    epoch_bytes[..8].copy_from_slice(&111u64.to_le_bytes());
    let pack2_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![LogicalEvent::AccessEpochRevoked {
            ticket_id,
            access_epoch: epoch_bytes,
        }],
    };
    let pack2_signed = dummy_signed_object(client_key, stream_client, 2, Some(pack1_id), 100);
    let pack2_id = append_pack_unchecked(&mut graph, pack2_signed, pack2_plain).expect("pack 2");

    // Pack 3: Operator tries to set InProgress
    let pack3_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x03; 16],
        events: vec![LogicalEvent::TicketStateChanged {
            ticket_id,
            new_state: 2, // InProgress
            epoch: 1,
        }],
    };
    let pack3_signed = dummy_signed_object(operator_key, stream_op, 1, None, 100);
    let pack3_id = append_pack_unchecked(&mut graph, pack3_signed, pack3_plain).expect("pack 3");

    // Admin attempts to pick Pack 3 as CanonicalHeadSet
    let head_set = CanonicalHeadSet::new(
        ticket_id,
        vec![pack1_id, pack2_id, pack3_id],
        crate::canonical::types::EntityId::from_bytes([0x99; 32]),
        2000,
    );
    graph.set_canonical_heads(head_set);

    let view = reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("ticket view");

    // HARD INVARIANT 13: CanonicalHeadSet MUST NOT grant shell access when client revoked it!
    assert!(
        !view.safety.permits_shell(),
        "Client safety revocation must remain authoritative"
    );
}

#[test]
fn test_chat_message_revision_audit_trail() {
    let mut graph = EventGraph::new();
    let ticket_id = TicketId::from_bytes([0x88; 16]);
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let resolver = SimpleRoleResolver::new().with_client(client_key);

    // Pack 1: TicketCreated
    let pack1_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![
            LogicalEvent::TicketCreated {
                ticket_id,
                title: "Network outage".into(),
                initial_epoch: 100,
            },
            LogicalEvent::ChatMessage {
                ticket_id,
                seq: 1,
                body: "Original statement with typo: server is down at 10.0.0.1".into(),
            },
        ],
    };
    let pack1_signed = dummy_signed_object(client_key, stream_id, 1, None, 100);
    let pack1_id = append_pack_unchecked(&mut graph, pack1_signed, pack1_plain).expect("pack 1");

    // Pack 2: ChatMessageRevised
    let pack2_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![LogicalEvent::ChatMessageRevised {
            ticket_id,
            original_seq: 1,
            replacement_body: "Corrected statement: server is down at 10.0.0.2".into(),
        }],
    };
    let pack2_signed = dummy_signed_object(client_key, stream_id, 2, Some(pack1_id), 100);
    append_pack_unchecked(&mut graph, pack2_signed, pack2_plain).expect("pack 2");

    let view = reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("ticket view");
    assert_eq!(view.messages.len(), 1);
    assert_eq!(
        view.messages[0].body,
        "Corrected statement: server is down at 10.0.0.2"
    );
    // Audit history preserved!
    assert_eq!(view.messages[0].edit_history.len(), 1);
    assert_eq!(
        view.messages[0].edit_history[0],
        "Original statement with typo: server is down at 10.0.0.1"
    );
}

#[test]
fn test_ticket_snapshot_cold_start_acceleration() {
    use crate::canonical::events::snapshot::{reduce_ticket_from_snapshot, TicketSnapshot};

    let mut graph = EventGraph::new();
    let ticket_id = TicketId::from_bytes([0xaa; 16]);
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let resolver = SimpleRoleResolver::new().with_client(client_key);

    // Pack 1: Created + message 1
    let pack1_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![
            LogicalEvent::TicketCreated {
                ticket_id,
                title: "Slow database query".into(),
                initial_epoch: 200,
            },
            LogicalEvent::ChatMessage {
                ticket_id,
                seq: 1,
                body: "Message 1".into(),
            },
        ],
    };
    let pack1_signed = dummy_signed_object(client_key, stream_id, 1, None, 100);
    let pack1_id = append_pack_unchecked(&mut graph, pack1_signed, pack1_plain).expect("pack 1");

    // Pack 2: message 2
    let pack2_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![LogicalEvent::ChatMessage {
            ticket_id,
            seq: 2,
            body: "Message 2".into(),
        }],
    };
    let pack2_signed = dummy_signed_object(client_key, stream_id, 2, Some(pack1_id), 100);
    let pack2_id = append_pack_unchecked(&mut graph, pack2_signed, pack2_plain).expect("pack 2");

    // Materialize state and create Snapshot
    let view_before = reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("view");
    assert_eq!(view_before.messages.len(), 2);
    assert_eq!(view_before.incorporated_packs, vec![pack1_id, pack2_id]);

    let snapshot = TicketSnapshot::create(&view_before, 5000);
    assert_eq!(snapshot.frontier_head_packs, vec![pack1_id, pack2_id]);

    // Now append tail Pack 3 to the graph
    let pack3_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x03; 16],
        events: vec![LogicalEvent::ChatMessage {
            ticket_id,
            seq: 3,
            body: "Tail Message 3 arrived after snapshot".into(),
        }],
    };
    let pack3_signed = dummy_signed_object(client_key, stream_id, 3, Some(pack2_id), 100);
    let pack3_id = append_pack_unchecked(&mut graph, pack3_signed, pack3_plain).expect("pack 3");

    // Fast cold start: apply tail events on top of the snapshot
    let view_accelerated = reduce_ticket_from_snapshot(&snapshot, &graph, &resolver);
    assert_eq!(view_accelerated.messages.len(), 3);
    assert_eq!(
        view_accelerated.messages[2].body,
        "Tail Message 3 arrived after snapshot"
    );
    assert_eq!(
        view_accelerated.incorporated_packs,
        vec![pack1_id, pack2_id, pack3_id]
    );
}

#[test]
fn test_local_search_index() {
    use crate::canonical::events::reducer::{AttachmentView, ChatMessageView};
    use crate::canonical::events::search::{LocalSearchIndex, MatchType};

    let mut search_index = LocalSearchIndex::new();
    let ticket_id = TicketId::from_bytes([0xee; 16]);
    let initial_epoch = AccessEpoch::from_bytes([0x11; 16]);

    let view = crate::canonical::events::reducer::TicketView {
        ticket_id,
        title: "VPN Gateway Connection Refused".into(),
        safety: TicketSafetyState::new_client_open(ticket_id, initial_epoch),
        messages: vec![ChatMessageView {
            pack_id: ObjectId::from_bytes([0x01; 32]),
            seq: 1,
            body: "Client cannot reach remote gateway IP 192.168.1.1".into(),
            edit_history: Vec::new(),
        }],
        attachments: vec![AttachmentView {
            pack_id: ObjectId::from_bytes([0x02; 32]),
            blob_id: BlobId::from_bytes([0x33; 32]),
            filename: "diagnostic-packet-dump.pcap".into(),
            size_bytes: 65536,
        }],
        incorporated_packs: vec![],
    };

    search_index.index_ticket(view);

    // Search matches title
    let res_title = search_index.search("gateway");
    assert!(!res_title.is_empty());
    assert!(res_title.iter().any(|r| r.match_type == MatchType::Title));

    // Search matches message body
    let res_body = search_index.search("192.168.1.1");
    assert_eq!(res_body.len(), 1);
    assert_eq!(res_body[0].match_type, MatchType::MessageBody);

    // Search matches attachment filename
    let res_att = search_index.search(".pcap");
    assert_eq!(res_att.len(), 1);
    assert_eq!(res_att[0].match_type, MatchType::AttachmentFilename);

    // Search nonexistent query
    let res_none = search_index.search("completely_unrelated_query");
    assert!(res_none.is_empty());
}

#[test]
fn test_eventpack_overhead_amortization_proof() {
    use crate::canonical::codec::to_canonical_cbor;

    let ticket_id = TicketId::from_bytes([0x12; 16]);

    // Scenario A: 32 individual EventPacks (each containing 1 message, signed & framed individually)
    let mut individual_bytes = 0usize;
    for i in 1..=32 {
        let single_pack = EventPackPlaintext {
            schema_version: 1,
            ticket_id: Some(ticket_id),
            ticket_crypto_epoch: Some(1),
            pack_nonce: [i as u8; 16],
            events: vec![LogicalEvent::ChatMessage {
                ticket_id,
                seq: i,
                body: format!("Short status message number {}", i),
            }],
        };
        let cbor = to_canonical_cbor(&single_pack).expect("cbor");
        let signed = dummy_signed_object(
            KeyId::from_bytes([0x01; 32]),
            StreamId::from_bytes([0x02; 16]),
            i,
            None,
            cbor.len() as u64,
        );
        let signed_cbor = to_canonical_cbor(&signed).expect("signed cbor");
        individual_bytes += cbor.len() + signed_cbor.len();
    }

    // Scenario B: 1 EventPack amortizing all 32 messages
    let batch_pack = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0xff; 16],
        events: (1..=32)
            .map(|i| LogicalEvent::ChatMessage {
                ticket_id,
                seq: i,
                body: format!("Short status message number {}", i),
            })
            .collect(),
    };
    let batch_cbor = to_canonical_cbor(&batch_pack).expect("cbor");
    let batch_signed = dummy_signed_object(
        KeyId::from_bytes([0x01; 32]),
        StreamId::from_bytes([0x02; 16]),
        1,
        None,
        batch_cbor.len() as u64,
    );
    let batch_signed_cbor = to_canonical_cbor(&batch_signed).expect("signed cbor");
    let batched_bytes = batch_cbor.len() + batch_signed_cbor.len();

    // PROOF: Batched representation consumes less than 40% of the wire framing overhead
    let overhead_ratio = (batched_bytes as f64) / (individual_bytes as f64);
    assert!(
        overhead_ratio < 0.40,
        "EventPack must achieve >60% overhead reduction vs individual messages; got ratio {}",
        overhead_ratio
    );
}

#[test]
fn test_verified_event_pack_typestate_and_signature_verification() {
    use crate::canonical::signing::{SigningError, Verifier};

    struct TestVerifier {
        expected_sig: Vec<u8>,
    }
    impl Verifier for TestVerifier {
        fn verify(
            &self,
            _domain_separated_data: &[u8],
            signature: &[u8],
        ) -> Result<(), SigningError> {
            if signature == self.expected_sig.as_slice() {
                Ok(())
            } else {
                Err(SigningError::VerificationFailed(
                    "signature mismatch".into(),
                ))
            }
        }
    }

    let mut graph = EventGraph::new();
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let signed_obj = dummy_signed_object(client_key, stream_id, 1, None, 100);
    let plaintext = EventPackPlaintext {
        schema_version: 1,
        ticket_id: None,
        ticket_crypto_epoch: None,
        pack_nonce: [0x01; 16],
        events: vec![],
    };

    // Case 1: Bad signature -> VerifiedEventPack::verify returns Err
    let bad_verifier = TestVerifier {
        expected_sig: vec![0xff; 64],
    };
    let err = VerifiedEventPack::verify(signed_obj.clone(), plaintext.clone(), &bad_verifier);
    assert!(err.is_err());

    // Case 2: Good signature -> VerifiedEventPack::verify returns Ok(verified)
    let good_verifier = TestVerifier {
        expected_sig: signed_obj.signature.clone(),
    };
    let verified =
        VerifiedEventPack::verify(signed_obj, plaintext, &good_verifier).expect("verification ok");
    let pack_id = graph.append_pack(verified).expect("append verified pack");
    assert!(graph.get_object(&pack_id).is_some());
}

#[test]
fn test_simple_role_resolver_fail_closed() {
    let mut graph = EventGraph::new();
    let ticket_id = TicketId::from_bytes([0x77; 16]);
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let unknown_key = KeyId::from_bytes([0x99; 32]);

    // Resolver ONLY recognizes client_key
    let resolver = SimpleRoleResolver::new().with_client(client_key);

    // Pack 1: Client creates ticket
    let pack1_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![LogicalEvent::TicketCreated {
            ticket_id,
            title: "Authorized Ticket".into(),
            initial_epoch: 100,
        }],
    };
    let pack1_signed = dummy_signed_object(client_key, stream_id, 1, None, 100);
    let pack1_id = append_pack_unchecked(&mut graph, pack1_signed, pack1_plain).expect("pack 1");

    // Pack 2: Unknown key attempts to inject a message and change ticket state
    let pack2_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![
            LogicalEvent::ChatMessage {
                ticket_id,
                seq: 1,
                body: "Malicious injection".into(),
            },
            LogicalEvent::TicketStateChanged {
                ticket_id,
                new_state: 3, // Closed
                epoch: 1,
            },
        ],
    };
    let pack2_signed = dummy_signed_object(unknown_key, stream_id, 2, Some(pack1_id), 100);
    append_pack_unchecked(&mut graph, pack2_signed, pack2_plain).expect("pack 2 append into graph");

    // Reduce: The unauthorized pack MUST be completely dropped by the fail-closed resolver!
    let view = reduce_ticket_with_resolver(ticket_id, &graph, &resolver).expect("ticket view");
    assert_eq!(view.title, "Authorized Ticket");
    assert_eq!(
        view.messages.len(),
        0,
        "Unauthorized message must be dropped"
    );
    assert_eq!(view.incorporated_packs, vec![pack1_id]);
}

#[test]
fn test_canonical_head_set_ancestry_retention() {
    let mut graph = EventGraph::new();
    let ticket_id = TicketId::from_bytes([0x88; 16]);
    let stream_id = StreamId::from_bytes([0x01; 16]);
    let client_key = KeyId::from_bytes([0x01; 32]);
    let resolver = SimpleRoleResolver::new().with_client(client_key);

    // Pack 1: Create ticket
    let pack1_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x01; 16],
        events: vec![LogicalEvent::TicketCreated {
            ticket_id,
            title: "Ancestry Ticket".into(),
            initial_epoch: 100,
        }],
    };
    let pack1_signed = dummy_signed_object(client_key, stream_id, 1, None, 100);
    let pack1_id = append_pack_unchecked(&mut graph, pack1_signed, pack1_plain).expect("pack 1");

    // Pack 2: First message (parent = pack1)
    let pack2_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x02; 16],
        events: vec![LogicalEvent::ChatMessage {
            ticket_id,
            seq: 1,
            body: "First message in ancestor chain".into(),
        }],
    };
    let pack2_signed = dummy_signed_object(client_key, stream_id, 2, Some(pack1_id), 100);
    let pack2_id = append_pack_unchecked(&mut graph, pack2_signed, pack2_plain).expect("pack 2");

    // Pack 3: Second message (parent = pack2)
    let pack3_plain = EventPackPlaintext {
        schema_version: 1,
        ticket_id: Some(ticket_id),
        ticket_crypto_epoch: Some(1),
        pack_nonce: [0x03; 16],
        events: vec![LogicalEvent::ChatMessage {
            ticket_id,
            seq: 2,
            body: "Second message at the head".into(),
        }],
    };
    let pack3_signed = dummy_signed_object(client_key, stream_id, 3, Some(pack2_id), 100);
    let pack3_id = append_pack_unchecked(&mut graph, pack3_signed, pack3_plain).expect("pack 3");

    // Admin sets CanonicalHeadSet containing ONLY the leaf head (pack3_id)
    let head_set = CanonicalHeadSet::new(
        ticket_id,
        vec![pack3_id], // ONLY the tip/leaf
        crate::canonical::types::EntityId::from_bytes([0x99; 32]),
        3000,
    );
    graph.set_canonical_heads(head_set);

    // Reduction MUST walk ancestry backwards from pack3 and include pack1 and pack2!
    let view = reduce_ticket_with_resolver(ticket_id, &graph, &resolver)
        .expect("ticket view must exist despite head set only containing leaf");
    assert_eq!(view.title, "Ancestry Ticket");
    assert_eq!(
        view.messages.len(),
        2,
        "Both ancestor messages must be present"
    );
    assert_eq!(view.messages[0].body, "First message in ancestor chain");
    assert_eq!(view.messages[1].body, "Second message at the head");
    assert_eq!(view.incorporated_packs, vec![pack1_id, pack2_id, pack3_id]);
}
