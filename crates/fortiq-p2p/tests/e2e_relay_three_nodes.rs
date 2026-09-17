#![allow(deprecated)]

use fortiq_core::{
    AuthorizationConfig, CapabilitiesConfig, Config, IdentityConfig, NetworkConfig, NodeConfig,
    NodeInfo, NodeMode, TicketConfig, TicketPriority, TicketState, TicketStore,
};
use fortiq_p2p::{P2pCommand, RunOptions, TicketSyncRequest, TicketSyncResponse};
use libp2p::{identity::Keypair, Multiaddr};
use std::time::Duration;

#[tokio::test]
async fn e2e_relay_rendezvous_three_nodes_interaction() {
    let _ = tracing_subscriber::fmt().with_test_writer().try_init();
    let relay_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };
    let managed_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };
    let operator_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };

    let relay_keypair = Keypair::generate_ed25519();
    let relay_peer_id = relay_keypair.public().to_peer_id();

    let managed_keypair = Keypair::generate_ed25519();
    let managed_peer_id = managed_keypair.public().to_peer_id();

    let operator_keypair = Keypair::generate_ed25519();
    let operator_peer_id = operator_keypair.public().to_peer_id();

    let dir_relay = tempfile::tempdir().unwrap();
    let dir_managed = tempfile::tempdir().unwrap();
    let dir_operator = tempfile::tempdir().unwrap();

    let relay_addr_str = format!("/ip4/127.0.0.1/udp/{relay_port}/quic-v1/p2p/{relay_peer_id}");
    let relay_multiaddr: Multiaddr = relay_addr_str.parse().unwrap();

    // 1. Relay Node configuration
    let relay_config = Config {
        node: NodeConfig {
            name: "relay-node".to_string(),
        },
        identity: IdentityConfig {
            path: dir_relay.path().join("id.key"),
        },
        authorization: AuthorizationConfig::default(),
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{relay_port}"),
            public_addr: Some(format!("/ip4/127.0.0.1/udp/{relay_port}/quic-v1")),
            relay_peer: None,
        },
        capabilities: CapabilitiesConfig {
            rendezvous: true,
            relay: true,
            dcutr: false,
            relay_rate_limit: false,
        },
        ticket: TicketConfig::default(),
        ipc: fortiq_core::IpcConfig::default(),
    };

    let relay_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{relay_port}/quic-v1")
        .parse()
        .unwrap();
    let relay_info = NodeInfo::local(relay_peer_id, "relay-node".to_string(), NodeMode::Managed);
    let relay_options = RunOptions {
        config: relay_config,
        listen_address: relay_listen_addr,
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        command_receiver: None,
    };

    // 2. Managed Node configuration
    let managed_ticket_path = dir_managed.path().join("ticket.json");
    let ticket_store = TicketStore::new(managed_ticket_path.clone());
    let opened_ticket = ticket_store
        .db()
        .create_ticket(
            "Relay test",
            "Ticket-scoped shell test",
            TicketPriority::Normal,
            &managed_peer_id.to_string(),
            &operator_peer_id.to_string(),
        )
        .unwrap();
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
            relay_peer: Some(relay_addr_str.clone()),
        },
        capabilities: CapabilitiesConfig {
            dcutr: false,
            ..Default::default()
        },
        ticket: TicketConfig {
            path: Some(managed_ticket_path.clone()),
        },
        ipc: fortiq_core::IpcConfig::default(),
    };

    let managed_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{managed_port}/quic-v1")
        .parse()
        .unwrap();
    let managed_info = NodeInfo::local(
        managed_peer_id,
        "managed-node".to_string(),
        NodeMode::Managed,
    );
    let managed_options = RunOptions {
        config: managed_config,
        listen_address: managed_listen_addr,
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        command_receiver: None,
    };

    // 3. Operator Node configuration
    let operator_config = Config {
        node: NodeConfig {
            name: "operator-node".to_string(),
        },
        identity: IdentityConfig {
            path: dir_operator.path().join("id.key"),
        },
        authorization: AuthorizationConfig::default(),
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{operator_port}"),
            public_addr: None,
            relay_peer: Some(relay_addr_str.clone()),
        },
        capabilities: CapabilitiesConfig {
            dcutr: false,
            ..Default::default()
        },
        ticket: TicketConfig::default(),
        ipc: fortiq_core::IpcConfig::default(),
    };

    let operator_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{operator_port}/quic-v1")
        .parse()
        .unwrap();
    let operator_info = NodeInfo::local(
        operator_peer_id,
        "operator-node".to_string(),
        NodeMode::Operator,
    );

    let (op_cmd_tx, op_cmd_rx) = tokio::sync::mpsc::channel(32);
    let operator_options = RunOptions {
        config: operator_config,
        listen_address: operator_listen_addr,
        dial_address: Some(relay_multiaddr.clone()),
        shell_peer: None,
        shell_command: None,
        command_receiver: Some(op_cmd_rx),
    };

    // Start all 3 nodes (give relay a moment to initialize before clients connect)
    let relay_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(relay_keypair, relay_info, relay_options).await;
    });
    tokio::time::sleep(Duration::from_millis(150)).await;

    let managed_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(managed_keypair, managed_info, managed_options).await;
    });

    let operator_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(operator_keypair, operator_info, operator_options).await;
    });

    // Step A: Wait for Operator to discover Managed node via Rendezvous & Relay
    let mut discovered = false;
    let mut discovered_peer = None;
    for _ in 0..50 {
        tokio::time::sleep(Duration::from_millis(200)).await;
        let (tx, rx) = tokio::sync::oneshot::channel();
        if op_cmd_tx
            .send(P2pCommand::ListPeers { reply: tx })
            .await
            .is_ok()
        {
            if let Ok(peers) = rx.await {
                if let Some(peer) = peers.iter().find(|p| {
                    p.peer_id == managed_peer_id.to_string() && p.hostname == "managed-node"
                }) {
                    discovered = true;
                    discovered_peer = Some(peer.clone());
                    break;
                }
            }
        }
    }
    assert!(
        discovered,
        "Operator did not discover Managed node via Rendezvous/Relay"
    );
    let peer_summary = discovered_peer.expect("Managed peer summary must exist");
    assert_eq!(
        peer_summary.transport, "RELAY CIRCUIT",
        "Transport must be strictly RELAY CIRCUIT without loopback fallback"
    );

    // Step B: Open first shell stream through relay & pending connection state machine
    let (shell_tx, shell_rx) = tokio::sync::oneshot::channel();
    op_cmd_tx
        .send(P2pCommand::OpenShellStream {
            peer: managed_peer_id,
            ticket_id: Some(opened_ticket.id.clone()),
            dial: None,
            reply: shell_tx,
        })
        .await
        .unwrap();

    let stream_res = tokio::time::timeout(Duration::from_secs(12), shell_rx)
        .await
        .expect("OpenShellStream timed out")
        .expect("Channel dropped");

    assert!(
        stream_res.is_err(),
        "Legacy shell/2.0 must be rejected by the hardened runtime: {:?}",
        stream_res
    );
    let err = stream_res.unwrap_err();
    assert!(
        err.contains("legacy shell/2.0 is disabled") || err.contains("shell/next"),
        "Unexpected legacy-shell rejection: {err}"
    );

    tokio::time::sleep(Duration::from_millis(200)).await;

    // Step D: Close the exact ticket through the ticket-aware protocol.
    let (close_tx, close_rx) = tokio::sync::oneshot::channel();
    op_cmd_tx
        .send(P2pCommand::SyncTickets {
            peer: managed_peer_id,
            dial: None,
            request: TicketSyncRequest::UpdateStatus {
                ticket_id: opened_ticket.id.clone(),
                state: TicketState::Closed,
            },
            reply: close_tx,
        })
        .await
        .unwrap();
    let close_response = tokio::time::timeout(Duration::from_secs(4), close_rx)
        .await
        .expect("ticket-aware close timed out")
        .expect("channel dropped")
        .expect("ticket-aware close failed");
    assert!(matches!(
        close_response,
        TicketSyncResponse::MutationApplied(_)
    ));

    // Step E: Verify ticket state is persisted as CLOSED on managed peer
    tokio::time::sleep(Duration::from_millis(300)).await;
    let current_ticket = ticket_store
        .get()
        .await
        .unwrap()
        .expect("ticket should exist");
    assert_eq!(current_ticket.state, TicketState::Closed);

    relay_handle.abort();
    managed_handle.abort();
    operator_handle.abort();
}

#[tokio::test]
async fn e2e_relay_production_rate_limiting_smoke() {
    let _ = tracing_subscriber::fmt().with_test_writer().try_init();
    let relay_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };
    let managed_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };
    let operator_port = {
        let s = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        s.local_addr().unwrap().port()
    };

    let relay_keypair = Keypair::generate_ed25519();
    let relay_peer_id = relay_keypair.public().to_peer_id();

    let managed_keypair = Keypair::generate_ed25519();
    let managed_peer_id = managed_keypair.public().to_peer_id();

    let operator_keypair = Keypair::generate_ed25519();
    let operator_peer_id = operator_keypair.public().to_peer_id();

    let dir_relay = tempfile::tempdir().unwrap();
    let dir_managed = tempfile::tempdir().unwrap();
    let dir_operator = tempfile::tempdir().unwrap();

    let relay_addr_str = format!("/ip4/127.0.0.1/udp/{relay_port}/quic-v1/p2p/{relay_peer_id}");
    let relay_multiaddr: Multiaddr = relay_addr_str.parse().unwrap();

    // 1. Relay Node configuration with production rate limiting enabled (relay_rate_limit: true)
    let relay_config = Config {
        node: NodeConfig {
            name: "relay-prod-smoke".to_string(),
        },
        identity: IdentityConfig {
            path: dir_relay.path().join("id.key"),
        },
        authorization: AuthorizationConfig::default(),
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{relay_port}"),
            public_addr: Some(format!("/ip4/127.0.0.1/udp/{relay_port}/quic-v1")),
            relay_peer: None,
        },
        capabilities: CapabilitiesConfig {
            rendezvous: true,
            relay: true,
            dcutr: false,
            relay_rate_limit: true,
        },
        ticket: TicketConfig::default(),
        ipc: fortiq_core::IpcConfig::default(),
    };

    let relay_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{relay_port}/quic-v1")
        .parse()
        .unwrap();
    let relay_info = NodeInfo::local(
        relay_peer_id,
        "relay-prod-smoke".to_string(),
        NodeMode::Managed,
    );
    let relay_options = RunOptions {
        config: relay_config,
        listen_address: relay_listen_addr,
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        command_receiver: None,
    };

    // 2. Managed Node configuration
    let managed_ticket_path = dir_managed.path().join("ticket.json");
    let ticket_store = TicketStore::new(managed_ticket_path.clone());
    let opened_ticket = ticket_store
        .db()
        .create_ticket(
            "Relay rate-limit test",
            "Ticket-scoped shell test",
            TicketPriority::Normal,
            &managed_peer_id.to_string(),
            &operator_peer_id.to_string(),
        )
        .unwrap();
    assert_eq!(opened_ticket.state, TicketState::Open);

    let managed_config = Config {
        node: NodeConfig {
            name: "managed-prod-smoke".to_string(),
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
            relay_peer: Some(relay_addr_str.clone()),
        },
        capabilities: CapabilitiesConfig {
            dcutr: false,
            ..Default::default()
        },
        ticket: TicketConfig {
            path: Some(managed_ticket_path.clone()),
        },
        ipc: fortiq_core::IpcConfig::default(),
    };

    let managed_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{managed_port}/quic-v1")
        .parse()
        .unwrap();
    let managed_info = NodeInfo::local(
        managed_peer_id,
        "managed-prod-smoke".to_string(),
        NodeMode::Managed,
    );
    let managed_options = RunOptions {
        config: managed_config,
        listen_address: managed_listen_addr,
        dial_address: None,
        shell_peer: None,
        shell_command: None,
        command_receiver: None,
    };

    // 3. Operator Node configuration
    let operator_config = Config {
        node: NodeConfig {
            name: "operator-prod-smoke".to_string(),
        },
        identity: IdentityConfig {
            path: dir_operator.path().join("id.key"),
        },
        authorization: AuthorizationConfig::default(),
        network: NetworkConfig {
            listen_quic: format!("127.0.0.1:{operator_port}"),
            public_addr: None,
            relay_peer: Some(relay_addr_str.clone()),
        },
        capabilities: CapabilitiesConfig {
            dcutr: false,
            ..Default::default()
        },
        ticket: TicketConfig::default(),
        ipc: fortiq_core::IpcConfig::default(),
    };

    let operator_listen_addr: Multiaddr = format!("/ip4/127.0.0.1/udp/{operator_port}/quic-v1")
        .parse()
        .unwrap();
    let operator_info = NodeInfo::local(
        operator_peer_id,
        "operator-prod-smoke".to_string(),
        NodeMode::Operator,
    );

    let (op_cmd_tx, op_cmd_rx) = tokio::sync::mpsc::channel(32);
    let operator_options = RunOptions {
        config: operator_config,
        listen_address: operator_listen_addr,
        dial_address: Some(relay_multiaddr.clone()),
        shell_peer: None,
        shell_command: None,
        command_receiver: Some(op_cmd_rx),
    };

    // Start all 3 nodes (give relay a moment to initialize before clients connect)
    let relay_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(relay_keypair, relay_info, relay_options).await;
    });
    tokio::time::sleep(Duration::from_millis(150)).await;

    let managed_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(managed_keypair, managed_info, managed_options).await;
    });

    let operator_handle = tokio::spawn(async move {
        let _ = fortiq_p2p::run(operator_keypair, operator_info, operator_options).await;
    });

    // Step A: Operator discovers Managed node via Rendezvous
    let mut discovered = false;
    let mut discovered_peer = None;
    for _ in 0..50 {
        tokio::time::sleep(Duration::from_millis(200)).await;
        let (reply_tx, reply_rx) = tokio::sync::oneshot::channel();
        if op_cmd_tx
            .send(P2pCommand::ListPeers { reply: reply_tx })
            .await
            .is_ok()
        {
            if let Ok(peers) = reply_rx.await {
                if let Some(peer) = peers.iter().find(|p| {
                    p.peer_id == managed_peer_id.to_string() && p.hostname == "managed-prod-smoke"
                }) {
                    discovered = true;
                    discovered_peer = Some(peer.clone());
                    break;
                }
            }
        }
    }
    assert!(
        discovered,
        "Operator did not discover Managed node under production rate limits"
    );
    let peer_summary = discovered_peer.expect("Managed peer summary must exist");
    assert_eq!(
        peer_summary.transport, "RELAY CIRCUIT",
        "Transport must be strictly RELAY CIRCUIT without loopback fallback"
    );

    // Step B: Open shell stream through rate-limited relay
    let (shell_tx, shell_rx) = tokio::sync::oneshot::channel();
    op_cmd_tx
        .send(P2pCommand::OpenShellStream {
            peer: managed_peer_id,
            ticket_id: Some(opened_ticket.id.clone()),
            dial: None,
            reply: shell_tx,
        })
        .await
        .unwrap();

    let stream_res = tokio::time::timeout(Duration::from_secs(12), shell_rx)
        .await
        .expect("OpenShellStream timed out")
        .expect("Channel dropped");

    assert!(
        stream_res.is_err(),
        "Legacy shell/2.0 should be denied under production rate limits: {:?}",
        stream_res
    );
    let err = stream_res.unwrap_err();
    assert!(
        err.contains("legacy shell/2.0 is disabled") || err.contains("shell/next"),
        "Unexpected legacy-shell rejection under rate limits: {err}"
    );

    tokio::time::sleep(Duration::from_millis(200)).await;

    // Step C: Close the exact ticket through the ticket-aware protocol.
    let (close_tx, close_rx) = tokio::sync::oneshot::channel();
    op_cmd_tx
        .send(P2pCommand::SyncTickets {
            peer: managed_peer_id,
            dial: None,
            request: TicketSyncRequest::UpdateStatus {
                ticket_id: opened_ticket.id.clone(),
                state: TicketState::Closed,
            },
            reply: close_tx,
        })
        .await
        .unwrap();
    let close_response = tokio::time::timeout(Duration::from_secs(4), close_rx)
        .await
        .expect("ticket-aware close timed out")
        .expect("channel dropped")
        .expect("ticket-aware close failed");
    assert!(matches!(
        close_response,
        TicketSyncResponse::MutationApplied(_)
    ));

    // Step D: Verify ticket state is persisted as CLOSED on managed peer
    tokio::time::sleep(Duration::from_millis(300)).await;
    let current_ticket = ticket_store
        .get()
        .await
        .unwrap()
        .expect("ticket should exist");
    assert_eq!(current_ticket.state, TicketState::Closed);

    relay_handle.abort();
    managed_handle.abort();
    operator_handle.abort();
}
