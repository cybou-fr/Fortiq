//! Tests for Phase 12 Self-Support ("This Device") and Local Loopback IPC.

use super::*;
use crate::canonical::shell::challenge::ShellAuthResponse;
use crate::canonical::signing::{Signer, SigningError, Verifier};
use crate::canonical::types::{EntityId, KeyId};

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
fn test_this_device_target_resolution_and_diagnostics() {
    let device_id = EntityId::from_bytes([0x42; 32]);
    let this_device = ThisDevice::new(device_id, LoopbackEndpoint::MemoryChannel);

    // Target resolution tests
    assert!(this_device.resolve_target(&SupportTarget::ThisDevice));
    assert!(this_device.resolve_target(&SupportTarget::RemotePeer(device_id)));
    assert!(
        !this_device.resolve_target(&SupportTarget::RemotePeer(EntityId::from_bytes([0x99; 32])))
    );

    // Diagnostics collection test
    let storage_summary = StorageDiagnostics {
        active_shards: 12,
        event_packs_stored: 48,
        canonical_heads: 2,
        local_storage_bytes: 1024 * 1024,
    };
    let diagnostics = this_device.collect_diagnostics(Some(storage_summary.clone()));

    assert!(diagnostics.is_loopback_active);
    assert!(
        diagnostics.relay_bypassed,
        "Self-support must bypass swarm relays"
    );
    assert_eq!(diagnostics.storage, storage_summary);
    assert!(!diagnostics.os.is_empty());
    assert!(!diagnostics.arch.is_empty());
    assert!(!diagnostics.hostname.is_empty());
}

#[tokio::test]
async fn test_local_ipc_framing_ping_pong_and_limits() {
    let (client_io, server_io) = tokio::io::duplex(1024 * 64);
    let mut client_framed = LocalIpcFramed::new(client_io);
    let mut server_framed = LocalIpcFramed::new(server_io);

    // Ping / Pong exchange
    client_framed
        .send_message(&LocalIpcMessage::Ping)
        .await
        .unwrap();
    let msg = server_framed.recv_message().await.unwrap().unwrap();
    assert_eq!(msg, LocalIpcMessage::Ping);

    server_framed
        .send_message(&LocalIpcMessage::Pong)
        .await
        .unwrap();
    let reply = client_framed.recv_message().await.unwrap().unwrap();
    assert_eq!(reply, LocalIpcMessage::Pong);
}

#[tokio::test]
async fn test_local_ipc_oversized_frame_rejection() {
    let (client_io, mut server_io) = tokio::io::duplex(1024 * 64);
    let mut client_framed = LocalIpcFramed::new(client_io);

    // Artificially send a length header claiming payload exceeds MAX_LOCAL_IPC_FRAME_SIZE
    use tokio::io::AsyncWriteExt;
    let oversize = (MAX_LOCAL_IPC_FRAME_SIZE + 1) as u32;
    server_io.write_all(&oversize.to_be_bytes()).await.unwrap();

    let err = client_framed.recv_message().await.unwrap_err();
    match err {
        LocalIpcError::FrameTooLarge(len) => assert_eq!(len, MAX_LOCAL_IPC_FRAME_SIZE + 1),
        other => panic!("Expected FrameTooLarge error, got {:?}", other),
    }
}

#[test]
fn test_self_support_ticket_lifecycle_and_shell_safety() {
    let device_id = EntityId::from_bytes([0x11; 32]);
    let this_device = ThisDevice::new(device_id, LoopbackEndpoint::MemoryChannel);
    let mut engine = SelfSupportEngine::new(this_device);

    let creator = EntityId::from_bytes([0x22; 32]);
    let ticket = engine
        .create_self_support_ticket(
            "Offline Diagnosis".to_string(),
            "Investigate local storage indices".to_string(),
            creator,
        )
        .expect("Self-support ticket creation must succeed");

    assert!(ticket.is_self_support);
    assert!(!ticket.is_closed);
    assert_eq!(
        engine.get_ticket(&ticket.ticket_id).unwrap().title,
        "Offline Diagnosis"
    );

    // Prepare shell challenge
    let challenge = engine
        .create_shell_challenge(&ticket.ticket_id, &ticket.current_epoch)
        .expect("Challenge creation must succeed");

    // Operator signs challenge
    let operator_signer = MockSigner {
        key_id: KeyId::from_bytes([0x33; 32]),
    };
    let operator_verifier = MockVerifier;
    let operator_entity = EntityId::from_bytes([0x44; 32]);
    let auth_resp =
        ShellAuthResponse::create(&operator_signer, operator_entity, &challenge).unwrap();

    // Authorize shell
    let guard = engine
        .authorize_and_open_shell(&challenge, &auth_resp, &operator_verifier)
        .expect("Shell authorization must succeed");

    assert!(!guard.is_revoked());
    assert!(!guard.cancellation_token().is_cancelled());

    // Instant client revocation
    engine
        .revoke_shell(
            &ticket.ticket_id,
            &ticket.current_epoch,
            "user requested stop",
        )
        .expect("Revocation must succeed");

    assert!(
        guard.is_revoked(),
        "Guard must be atomically marked revoked"
    );
    assert!(
        guard.cancellation_token().is_cancelled(),
        "Token must be cancelled"
    );

    // Anti-resurrection invariant: Old epoch cannot be reused
    let err = engine
        .create_shell_challenge(&ticket.ticket_id, &ticket.current_epoch)
        .unwrap_err();
    match err {
        SelfSupportError::Auth(_) => {}
        other => panic!("Expected Auth error for revoked epoch, got {:?}", other),
    }

    // Closing ticket revokes and disables new sessions
    engine
        .close_ticket(&ticket.ticket_id, "Ticket resolved")
        .unwrap();
    let closed_err = engine
        .create_shell_challenge(&ticket.ticket_id, &ticket.current_epoch)
        .unwrap_err();
    assert!(matches!(closed_err, SelfSupportError::TicketClosed));
}

#[tokio::test]
async fn test_full_loopback_ipc_self_support_workflow() {
    let device_id = EntityId::from_bytes([0x55; 32]);
    let this_device = ThisDevice::new(device_id, LoopbackEndpoint::MemoryChannel);
    let mut engine = SelfSupportEngine::new(this_device);

    let (client_io, server_io) = tokio::io::duplex(1024 * 64);
    let mut client = LocalIpcFramed::new(client_io);
    let mut server = LocalIpcFramed::new(server_io);

    // 1. Handshake
    let client_id = EntityId::from_bytes([0x77; 32]);
    client
        .send_message(&LocalIpcMessage::Handshake {
            client_id,
            client_version: "3.0.0".to_string(),
        })
        .await
        .unwrap();

    let handshake_msg = server.recv_message().await.unwrap().unwrap();
    if let LocalIpcMessage::Handshake { .. } = handshake_msg {
        server
            .send_message(&LocalIpcMessage::HandshakeAck {
                device_id,
                is_this_device: true,
            })
            .await
            .unwrap();
    } else {
        panic!("Expected Handshake message");
    }

    let ack = client.recv_message().await.unwrap().unwrap();
    assert_eq!(
        ack,
        LocalIpcMessage::HandshakeAck {
            device_id,
            is_this_device: true,
        }
    );

    // 2. Request Diagnostics over Loopback IPC
    client
        .send_message(&LocalIpcMessage::DiagnosticsRequest)
        .await
        .unwrap();
    let diag_req = server.recv_message().await.unwrap().unwrap();
    assert_eq!(diag_req, LocalIpcMessage::DiagnosticsRequest);

    let diag = engine.collect_diagnostics(None);
    server
        .send_message(&LocalIpcMessage::DiagnosticsResponse(diag.clone()))
        .await
        .unwrap();

    let diag_resp = client.recv_message().await.unwrap().unwrap();
    assert_eq!(diag_resp, LocalIpcMessage::DiagnosticsResponse(diag));

    // 3. Create Self-Support Ticket over Loopback IPC
    client
        .send_message(&LocalIpcMessage::CreateSelfSupportTicket {
            title: "Loopback Diagnostics".to_string(),
            description: "Offline health scan".to_string(),
            creator_id: client_id,
        })
        .await
        .unwrap();

    let create_msg = server.recv_message().await.unwrap().unwrap();
    let (ticket_id, epoch) = if let LocalIpcMessage::CreateSelfSupportTicket {
        title,
        description,
        creator_id,
    } = create_msg
    {
        let ticket = engine
            .create_self_support_ticket(title, description, creator_id)
            .unwrap();
        server
            .send_message(&LocalIpcMessage::SelfSupportTicketCreated {
                ticket_id: ticket.ticket_id,
                initial_epoch: ticket.current_epoch,
            })
            .await
            .unwrap();
        (ticket.ticket_id, ticket.current_epoch)
    } else {
        panic!("Expected CreateSelfSupportTicket");
    };

    let created_resp = client.recv_message().await.unwrap().unwrap();
    assert_eq!(
        created_resp,
        LocalIpcMessage::SelfSupportTicketCreated {
            ticket_id,
            initial_epoch: epoch,
        }
    );

    // 4. Stream data over Loopback IPC
    let test_data = b"diagnostics: memory 100% OK, zero corruption".to_vec();
    client
        .send_message(&LocalIpcMessage::ShellStreamData(test_data.clone()))
        .await
        .unwrap();
    let stream_msg = server.recv_message().await.unwrap().unwrap();
    assert_eq!(stream_msg, LocalIpcMessage::ShellStreamData(test_data));
}
