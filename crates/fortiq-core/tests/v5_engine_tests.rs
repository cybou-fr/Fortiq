use anyhow::Result;
use fortiq_core::{
    AuthorityPolicy, Ed25519Signer, FsObjectStore, TicketEngine, TicketPriority, TicketState,
};
use tempfile::tempdir;

#[test]
fn test_engine_ticket_lifecycle() -> Result<()> {
    let dir = tempdir()?;
    let signer = Ed25519Signer::generate();
    let policy = AuthorityPolicy::default();

    let engine = TicketEngine::open(dir.path(), signer, policy)?;

    // 1. Create ticket
    let ticket = engine.create_ticket(
        "Service Outage",
        "Cannot connect to database",
        TicketPriority::Urgent,
        "peer-client-123",
    )?;

    assert_eq!(ticket.state, TicketState::Open);
    assert_eq!(ticket.priority, TicketPriority::Urgent);
    assert_eq!(ticket.revision, 1);

    // 2. Add message
    let msg = fortiq_core::ChatMessage {
        id: "M-1".into(),
        ticket_id: ticket.id.clone(),
        sender_peer_id: "peer-client-123".into(),
        body: "I have uploaded the error logs".into(),
        created_at: 1_000,
        delivery_state: "PENDING".into(),
    };
    engine.add_chat_message(&msg)?;

    let messages = engine.list_messages(&ticket.id)?;
    assert_eq!(messages.len(), 1);
    assert_eq!(messages[0].body, "I have uploaded the error logs");

    // 3. Add attachment
    let att = fortiq_core::AttachmentRecord {
        id: "A-1".into(),
        ticket_id: ticket.id.clone(),
        sender_peer_id: "peer-client-123".into(),
        filename: "syslog.txt".into(),
        size_bytes: 1024,
        sha256: "sha256dummy".into(),
        local_path: "/tmp/syslog.txt".into(),
        created_at: 1_005,
        state: "AVAILABLE".into(),
    };
    engine.add_attachment(&att)?;

    // 4. Update status to IN_PROGRESS
    let updated = engine.update_ticket_state(&ticket.id, TicketState::InProgress, "peer-operator-456")?;
    assert_eq!(updated.state, TicketState::InProgress);
    assert_eq!(updated.revision, 4); // created (1), msg (2), att (3), status (4)

    // 5. Check detail
    let detail = engine.get_ticket_detail(&ticket.id)?.unwrap();
    assert_eq!(detail.messages.len(), 1);
    assert_eq!(detail.attachments.len(), 1);
    assert_eq!(detail.events.len(), 2); // CREATED, STATUS_CHANGED

    Ok(())
}

#[test]
fn test_engine_crash_recovery_from_disk() -> Result<()> {
    let dir = tempdir()?;
    let signer = Ed25519Signer::generate();
    let client_id = "client-alpha".to_string();

    let ticket_id = {
        let engine = TicketEngine::open(dir.path(), signer, AuthorityPolicy::default())?;
        let t = engine.create_ticket(
            "Disk space warning",
            "/var is 95% full",
            TicketPriority::Normal,
            &client_id,
        )?;

        let msg = fortiq_core::ChatMessage {
            id: "M-1".into(),
            ticket_id: t.id.clone(),
            sender_peer_id: client_id.clone(),
            body: "Cleaned /tmp, usage down to 80%".into(),
            created_at: 1_000,
            delivery_state: "PENDING".into(),
        };
        engine.add_chat_message(&msg)?;

        engine.update_ticket_state(&t.id, TicketState::Resolved, "operator-beta")?;

        t.id
    }; // engine is dropped here!

    // Reopen from disk with a fresh engine instance
    let reopened_engine =
        TicketEngine::open(dir.path(), Ed25519Signer::generate(), AuthorityPolicy::default())?;

    let t = reopened_engine.get_ticket(&ticket_id)?.expect("ticket must be restored from disk");
    assert_eq!(t.state, TicketState::Resolved);
    assert_eq!(t.revision, 3);

    let messages = reopened_engine.list_messages(&ticket_id)?;
    assert_eq!(messages.len(), 1);
    assert_eq!(messages[0].body, "Cleaned /tmp, usage down to 80%");

    Ok(())
}

#[test]
fn test_engine_p2p_sync_remote_object() -> Result<()> {
    let dir_a = tempdir()?;
    let dir_b = tempdir()?;

    let signer_a = Ed25519Signer::generate();
    let signer_b = Ed25519Signer::generate();

    let engine_a = TicketEngine::open(dir_a.path(), signer_a, AuthorityPolicy::default())?;
    let engine_b = TicketEngine::open(dir_b.path(), signer_b, AuthorityPolicy::default())?;

    // Node A creates a ticket
    let ticket_a = engine_a.create_ticket(
        "Sync test",
        "Testing cross-node sync",
        TicketPriority::High,
        "peer-a",
    )?;

    // Export all objects from Node A's store
    let store_a = FsObjectStore::open(dir_a.path())?;
    let objects = store_a.load_all_objects()?;
    assert!(!objects.is_empty());

    // Node B receives and ingests the signed objects
    for obj in &objects {
        engine_b.apply_remote_object(obj)?;
    }

    // Node B now has the ticket with identical in-memory state!
    let ticket_b = engine_b
        .get_ticket(&ticket_a.id)?
        .expect("ticket must be present on Node B after sync");

    assert_eq!(ticket_a, ticket_b);

    Ok(())
}
