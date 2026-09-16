use fortiq_core::{
    AuthorizationConfig, CapabilitiesConfig, Config, IdentityConfig, NetworkConfig, NodeConfig,
    NodeInfo, NodeMode, TicketConfig, TicketState, TicketStore,
};
use fortiq_p2p::{P2pCommand, RunOptions};
use libp2p::{identity::Keypair, Multiaddr};
use std::time::Duration;

#[tokio::test]
async fn e2e_managed_operator_quic_interaction() {
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

    let managed_ticket_path = dir_managed.path().join("ticket.json");
    let ticket_store = TicketStore::new(managed_ticket_path.clone());
    let opened_ticket = ticket_store.open().await.unwrap();
    assert_eq!(opened_ticket.state, TicketState::Open);

    let managed_config = Config {
        node: NodeConfig {
            name: "managed-node".to_string(),
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
    };

    let operator_config = Config {
        node: NodeConfig {
            name: "operator-node".to_string(),
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
        ticket: TicketConfig::default(),
    };

    let managed_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{managed_port}/quic-v1")
        .parse()
        .unwrap();
    let operator_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{operator_port}/quic-v1")
        .parse()
        .unwrap();

    let managed_info = NodeInfo::local(
        managed_peer_id,
        "managed-node".to_string(),
        NodeMode::Managed,
    );
    let operator_info = NodeInfo::local(
        operator_peer_id,
        "operator-node".to_string(),
        NodeMode::Operator,
    );

    let managed_options = RunOptions {
        config: managed_config,
        listen_address: managed_listen_addr.clone(),
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        close_ticket_peer: None,
        command_receiver: None,
    };

    let (op_cmd_tx, op_cmd_rx) = tokio::sync::mpsc::channel(32);

    let mut managed_dial_addr = managed_listen_addr.clone();
    managed_dial_addr.push(libp2p::multiaddr::Protocol::P2p(managed_peer_id));

    let operator_options = RunOptions {
        config: operator_config,
        listen_address: operator_listen_addr,
        dial_address: Some(managed_dial_addr.clone()),
        shell_peer: None,
        shell_command: None,
        close_ticket_peer: None,
        command_receiver: Some(op_cmd_rx),
    };

    let managed_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(managed_keypair, managed_info, managed_options).await;
    });

    let operator_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(operator_keypair, operator_info, operator_options).await;
    });

    // Give peers time to dial and perform HELLO handshake
    let mut discovered = false;
    for _ in 0..30 {
        tokio::time::sleep(Duration::from_millis(200)).await;
        let (tx, rx) = tokio::sync::oneshot::channel();
        if op_cmd_tx
            .send(P2pCommand::ListPeers { reply: tx })
            .await
            .is_ok()
        {
            if let Ok(peers) = rx.await {
                if let Some(peer) = peers
                    .iter()
                    .find(|p| p.peer_id == managed_peer_id.to_string())
                {
                    assert_eq!(peer.hostname, "managed-node");
                    discovered = true;
                    break;
                }
            }
        }
    }
    assert!(
        discovered,
        "Operator did not discover Managed node via HELLO handshake"
    );

    // Close the ticket remotely as the authorized operator
    let (tx, rx) = tokio::sync::oneshot::channel();
    op_cmd_tx
        .send(P2pCommand::CloseTicket {
            peer: managed_peer_id,
            dial: Some(managed_dial_addr),
            reply: tx,
        })
        .await
        .unwrap();

    let close_result = rx.await.unwrap();
    assert!(
        close_result.is_ok(),
        "Remote ticket close request failed: {:?}",
        close_result
    );

    // Verify ticket state is persisted as CLOSED on managed peer
    tokio::time::sleep(Duration::from_millis(300)).await;
    let current_ticket = ticket_store
        .get()
        .await
        .unwrap()
        .expect("ticket should exist");
    assert_eq!(current_ticket.state, TicketState::Closed);

    managed_handle.abort();
    operator_handle.abort();
}

#[tokio::test]
async fn e2e_unauthorized_operator_rejected_on_ticket_close() {
    let managed_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };
    let intruder_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };

    let legit_operator_keypair = Keypair::generate_ed25519();
    let legit_operator_peer_id = legit_operator_keypair.public().to_peer_id();

    let intruder_keypair = Keypair::generate_ed25519();
    let intruder_peer_id = intruder_keypair.public().to_peer_id();

    let managed_keypair = Keypair::generate_ed25519();
    let managed_peer_id = managed_keypair.public().to_peer_id();

    let dir_managed = tempfile::tempdir().unwrap();
    let dir_intruder = tempfile::tempdir().unwrap();

    let managed_ticket_path = dir_managed.path().join("ticket.json");
    let ticket_store = TicketStore::new(managed_ticket_path.clone());
    let opened_ticket = ticket_store.open().await.unwrap();
    assert_eq!(opened_ticket.state, TicketState::Open);

    // Managed node ONLY trusts legit_operator_peer_id
    let managed_config = Config {
        node: NodeConfig {
            name: "managed-node".to_string(),
        },
        identity: IdentityConfig {
            path: dir_managed.path().join("id.key"),
        },
        authorization: AuthorizationConfig {
            operator_peer_id: Some(legit_operator_peer_id.to_string()),
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
    };

    let intruder_config = Config {
        node: NodeConfig {
            name: "intruder-node".to_string(),
        },
        identity: IdentityConfig {
            path: dir_intruder.path().join("id.key"),
        },
        authorization: AuthorizationConfig {
            operator_peer_id: None,
        },
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{intruder_port}"),
            public_addr: None,
            relay_peer: None,
        },
        capabilities: CapabilitiesConfig::default(),
        ticket: TicketConfig::default(),
    };

    let managed_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{managed_port}/quic-v1")
        .parse()
        .unwrap();
    let intruder_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{intruder_port}/quic-v1")
        .parse()
        .unwrap();

    let managed_info = NodeInfo::local(
        managed_peer_id,
        "managed-node".to_string(),
        NodeMode::Managed,
    );
    let intruder_info = NodeInfo::local(
        intruder_peer_id,
        "intruder-node".to_string(),
        NodeMode::Operator,
    );

    let managed_options = RunOptions {
        config: managed_config,
        listen_address: managed_listen_addr.clone(),
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        close_ticket_peer: None,
        command_receiver: None,
    };

    let (intruder_cmd_tx, intruder_cmd_rx) = tokio::sync::mpsc::channel(32);

    let mut managed_dial_addr = managed_listen_addr.clone();
    managed_dial_addr.push(libp2p::multiaddr::Protocol::P2p(managed_peer_id));

    let intruder_options = RunOptions {
        config: intruder_config,
        listen_address: intruder_listen_addr,
        dial_address: Some(managed_dial_addr.clone()),
        shell_peer: None,
        shell_command: None,
        close_ticket_peer: None,
        command_receiver: Some(intruder_cmd_rx),
    };

    let managed_handle = tokio::spawn(async move {
        if let Err(e) = fortiq_p2p::run(managed_keypair, managed_info, managed_options).await {
            eprintln!("Managed node failed: {e:?}");
        }
    });

    let intruder_handle = tokio::spawn(async move {
        if let Err(e) = fortiq_p2p::run(intruder_keypair, intruder_info, intruder_options).await {
            eprintln!("Intruder node failed: {e:?}");
        }
    });

    // Give time to connect
    tokio::time::sleep(Duration::from_millis(600)).await;

    // Intruder attempts to close ticket
    let (tx, rx) = tokio::sync::oneshot::channel();
    intruder_cmd_tx
        .send(P2pCommand::CloseTicket {
            peer: managed_peer_id,
            dial: Some(managed_dial_addr),
            reply: tx,
        })
        .await
        .unwrap();

    let close_result = rx.await.unwrap();
    assert!(
        close_result.is_err(),
        "Intruder should have been rejected from closing the ticket!"
    );

    // Ticket must remain OPEN
    let current_ticket = ticket_store
        .get()
        .await
        .unwrap()
        .expect("ticket should exist");
    assert_eq!(current_ticket.state, TicketState::Open);

    managed_handle.abort();
    intruder_handle.abort();
}
