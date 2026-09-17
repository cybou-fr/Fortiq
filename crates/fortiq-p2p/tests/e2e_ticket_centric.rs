#![allow(deprecated)]

use fortiq_core::{
    AuthorizationConfig, CapabilitiesConfig, Config, IdentityConfig, NetworkConfig, NodeConfig,
    NodeInfo, NodeMode, TicketConfig, TicketPriority, TicketState, TicketStore,
};
use fortiq_p2p::{ChatMessageWire, P2pCommand, RunOptions, TicketSyncRequest, TicketSyncResponse};
use libp2p::{identity::Keypair, Multiaddr};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

#[tokio::test]
async fn e2e_ticket_centric_full_lifecycle() {
    let managed_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };
    let operator_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };

    let managed_keypair = Keypair::generate_ed25519();
    let managed_peer_id = managed_keypair.public().to_peer_id();

    let operator_keypair = Keypair::generate_ed25519();
    let operator_peer_id = operator_keypair.public().to_peer_id();

    let dir_managed = tempfile::tempdir().unwrap();
    let dir_operator = tempfile::tempdir().unwrap();

    let managed_ticket_path = dir_managed.path().join("fortiq.toml");
    let operator_ticket_path = dir_operator.path().join("fortiq.toml");

    let managed_store = TicketStore::new(managed_ticket_path.clone());
    let operator_store = TicketStore::new(operator_ticket_path.clone());

    let managed_config = Config {
        node: NodeConfig {
            name: "managed-pc".to_string(),
        },
        identity: IdentityConfig {
            path: dir_managed.path().join("id.key"),
        },
        authorization: AuthorizationConfig {
            operator_peer_id: Some(operator_peer_id.to_string()),
        },
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{managed_port}"),
            public_addr: None,
            relay_peer: None,
        },
        capabilities: CapabilitiesConfig::default(),
        ticket: TicketConfig {
            path: Some(managed_ticket_path.clone()),
        },
        ipc: fortiq_core::IpcConfig::default(),
    };

    let operator_config = Config {
        node: NodeConfig {
            name: "operator-console".to_string(),
        },
        identity: IdentityConfig {
            path: dir_operator.path().join("id.key"),
        },
        authorization: AuthorizationConfig {
            operator_peer_id: None,
        },
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{operator_port}"),
            public_addr: None,
            relay_peer: None,
        },
        capabilities: CapabilitiesConfig::default(),
        ticket: TicketConfig {
            path: Some(operator_ticket_path.clone()),
        },
        ipc: fortiq_core::IpcConfig::default(),
    };

    let managed_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{managed_port}/quic-v1")
        .parse()
        .unwrap();
    let operator_listen_addr: Multiaddr = "/ip4/127.0.0.1/udp/0/quic-v1".parse().unwrap();

    let managed_info =
        NodeInfo::local(managed_peer_id, "managed-pc".to_string(), NodeMode::Managed);
    let operator_info = NodeInfo::local(
        operator_peer_id,
        "operator-console".to_string(),
        NodeMode::Operator,
    );

    let (managed_cmd_tx, managed_cmd_rx) = tokio::sync::mpsc::channel(32);
    let (operator_cmd_tx, operator_cmd_rx) = tokio::sync::mpsc::channel(32);

    let mut managed_dial_addr = managed_listen_addr.clone();
    managed_dial_addr.push(libp2p::multiaddr::Protocol::P2p(managed_peer_id));

    let managed_options = RunOptions {
        config: managed_config,
        listen_address: managed_listen_addr.clone(),
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        command_receiver: Some(managed_cmd_rx),
    };

    let operator_options = RunOptions {
        config: operator_config,
        listen_address: operator_listen_addr,
        dial_address: Some(managed_dial_addr.clone()),
        shell_peer: None,
        shell_command: None,
        command_receiver: Some(operator_cmd_rx),
    };

    let managed_handle = tokio::spawn(async move {
        if let Err(e) = fortiq_p2p::run(managed_keypair, managed_info, managed_options).await {
            eprintln!("Managed node error: {e:?}");
        }
    });

    tokio::time::sleep(Duration::from_millis(150)).await;

    let operator_handle = tokio::spawn(async move {
        if let Err(e) = fortiq_p2p::run(operator_keypair, operator_info, operator_options).await {
            eprintln!("Operator node error: {e:?}");
        }
    });

    // 1. Wait for peer discovery via HELLO
    let mut discovered = false;
    for _ in 0..40 {
        tokio::time::sleep(Duration::from_millis(150)).await;
        let (tx, rx) = tokio::sync::oneshot::channel();
        if operator_cmd_tx
            .send(P2pCommand::ListPeers { reply: tx })
            .await
            .is_ok()
        {
            if let Ok(peers) = rx.await {
                if peers
                    .iter()
                    .any(|p| p.peer_id == managed_peer_id.to_string())
                {
                    discovered = true;
                    break;
                }
            }
        }
    }
    assert!(discovered, "Operator failed to discover Managed node");

    // 2. Managed node creates a Ticket in SQLite
    let created_ticket = managed_store
        .db()
        .create_ticket(
            "Panne applicative critique",
            "L'application métier plante au démarrage",
            TicketPriority::Urgent,
            &managed_peer_id.to_string(),
            &operator_peer_id.to_string(),
        )
        .expect("failed to create ticket on managed node");
    assert_eq!(created_ticket.state, TicketState::Open);

    // 3. Operator syncs active ticket via /fortiq/ticket/2.0
    let (sync_tx, sync_rx) = tokio::sync::oneshot::channel();
    operator_cmd_tx
        .send(P2pCommand::SyncTickets {
            peer: managed_peer_id,
            dial: Some(managed_dial_addr.clone()),
            request: TicketSyncRequest::GetTicket {
                ticket_id: created_ticket.id.clone(),
            },
            reply: sync_tx,
        })
        .await
        .unwrap();

    let sync_res = sync_rx.await.unwrap().expect("ticket sync failed");
    match sync_res {
        TicketSyncResponse::Ticket(Some(synced)) => {
            assert_eq!(synced.id, created_ticket.id);
            assert_eq!(synced.title, created_ticket.title);
            assert_eq!(synced.state, TicketState::Open);
        }
        other => panic!("Unexpected ticket sync response: {other:?}"),
    }

    // Also import into operator local db for local message/attachment indexing
    operator_store.db().import_ticket(&created_ticket).unwrap();

    // 4. Bidirectional Chat via /fortiq/chat/1.0
    // (a) Operator -> Managed
    let op_msg_id = uuid::Uuid::new_v4().to_string();
    let (chat_tx, chat_rx) = tokio::sync::oneshot::channel();
    operator_cmd_tx
        .send(P2pCommand::SendChatMessage {
            peer: managed_peer_id,
            dial: Some(managed_dial_addr.clone()),
            message: ChatMessageWire {
                id: op_msg_id.clone(),
                ticket_id: created_ticket.id.clone(),
                body: "Bonjour, je prends en charge votre demande.".to_string(),
                created_at: now_secs(),
            },
            reply: chat_tx,
        })
        .await
        .unwrap();

    let ack1 = chat_rx.await.unwrap().expect("chat delivery failed");
    assert!(ack1.success);
    assert_eq!(ack1.message_id, op_msg_id);

    // Verify Managed DB recorded the incoming message
    let managed_msgs = managed_store
        .db()
        .list_messages(&created_ticket.id)
        .unwrap();
    assert_eq!(managed_msgs.len(), 1);
    assert_eq!(
        managed_msgs[0].body,
        "Bonjour, je prends en charge votre demande."
    );
    assert_eq!(managed_msgs[0].sender_peer_id, operator_peer_id.to_string());

    // (b) Managed -> Operator
    let client_msg_id = uuid::Uuid::new_v4().to_string();
    let (chat2_tx, chat2_rx) = tokio::sync::oneshot::channel();
    managed_cmd_tx
        .send(P2pCommand::SendChatMessage {
            peer: operator_peer_id,
            dial: None,
            message: ChatMessageWire {
                id: client_msg_id.clone(),
                ticket_id: created_ticket.id.clone(),
                body: "Merci, voici les logs d'erreur.".to_string(),
                created_at: now_secs(),
            },
            reply: chat2_tx,
        })
        .await
        .unwrap();

    let ack2 = chat2_rx
        .await
        .unwrap()
        .expect("client chat delivery failed");
    assert!(ack2.success);
    assert_eq!(ack2.message_id, client_msg_id);

    let op_msgs = operator_store
        .db()
        .list_messages(&created_ticket.id)
        .unwrap();
    assert!(op_msgs
        .iter()
        .any(|m| m.body == "Merci, voici les logs d'erreur."));

    // 5. File Transfer via /fortiq/file/1.0
    let test_file_path = dir_operator.path().join("crash_report.log");
    let test_file_data =
        b"FATAL ERROR: Exception 0xC0000005 at memory address 0x7FFAA010\nStack trace: ...\n";
    tokio::fs::write(&test_file_path, test_file_data)
        .await
        .unwrap();

    let (file_tx, file_rx) = tokio::sync::oneshot::channel();
    operator_cmd_tx
        .send(P2pCommand::SendFile {
            peer: managed_peer_id,
            dial: Some(managed_dial_addr.clone()),
            ticket_id: created_ticket.id.clone(),
            file_path: test_file_path,
            reply: file_tx,
        })
        .await
        .unwrap();

    let file_att = file_rx.await.unwrap().expect("file transfer failed");
    assert_eq!(file_att.filename, "crash_report.log");
    assert_eq!(file_att.size_bytes, test_file_data.len() as u64);

    // Verify Managed DB received attachment and file saved to disk
    tokio::time::sleep(Duration::from_millis(300)).await;
    let managed_attachments = managed_store
        .db()
        .list_attachments(&created_ticket.id)
        .unwrap();
    assert_eq!(managed_attachments.len(), 1);
    assert_eq!(managed_attachments[0].filename, "crash_report.log");
    assert_eq!(
        managed_attachments[0].size_bytes,
        test_file_data.len() as u64
    );

    let saved_file = tokio::fs::read(&managed_attachments[0].local_path)
        .await
        .unwrap();
    assert_eq!(saved_file, test_file_data);

    // 6. Operator opens Shell Stream bound to ticket -> Succeeds
    let (shell1_tx, shell1_rx) = tokio::sync::oneshot::channel();
    operator_cmd_tx
        .send(P2pCommand::OpenShellStream {
            peer: managed_peer_id,
            ticket_id: Some(created_ticket.id.clone()),
            dial: Some(managed_dial_addr.clone()),
            reply: shell1_tx,
        })
        .await
        .unwrap();

    let shell1_res = shell1_rx.await.unwrap();
    assert!(
        shell1_res.is_err(),
        "Legacy shell/2.0 must be rejected, even with a valid ticket: {:?}",
        shell1_res
    );
    let shell1_err = shell1_res.unwrap_err();
    assert!(
        shell1_err.contains("legacy shell/2.0 is disabled") || shell1_err.contains("shell/next"),
        "Unexpected legacy-shell denial: {shell1_err}"
    );
    tokio::time::sleep(Duration::from_millis(300)).await;

    // 7. Lifecycle closure is the sole ticket-level shell gate.
    managed_store
        .db()
        .update_ticket_state(
            &created_ticket.id,
            TicketState::Closed,
            &operator_peer_id.to_string(),
        )
        .unwrap();
    assert_eq!(
        managed_store
            .db()
            .get_ticket(&created_ticket.id)
            .unwrap()
            .unwrap()
            .state,
        TicketState::Closed
    );

    managed_handle.abort();
    operator_handle.abort();
}
