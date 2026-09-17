use fortiq_desktop_lib::CanonicalDesktopState;

#[tokio::test]
async fn test_desktop_self_support_diagnostics() {
    let state = CanonicalDesktopState::new_default();
    let diag = state.get_diagnostics().await;

    assert!(
        diag.is_loopback_active,
        "Loopback diagnostics must be active"
    );
    assert!(diag.relay_bypassed, "Self-support must bypass swarm relays");
    assert!(!diag.os.is_empty(), "OS must be detected");
    assert!(!diag.arch.is_empty(), "Arch must be detected");
    assert_eq!(diag.active_shards, 12);
    assert_eq!(diag.event_packs_stored, 24);
    assert_eq!(diag.canonical_heads, 1);
}

#[tokio::test]
async fn test_desktop_create_self_support_ticket() {
    let state = CanonicalDesktopState::new_default();
    let ticket = state
        .create_ticket(
            "Panne loopback test".to_string(),
            "Diagnostic local sans réseau externe".to_string(),
        )
        .await
        .expect("Self-support ticket creation should succeed");

    assert_eq!(ticket.title, "Panne loopback test");
    assert_eq!(ticket.description, "Diagnostic local sans réseau externe");
    assert_eq!(
        ticket.ticket_id.len(),
        32,
        "TicketId must be 16-byte hex (32 chars)"
    );
    assert_eq!(
        ticket.access_epoch.len(),
        32,
        "AccessEpoch must be 16-byte hex (32 chars)"
    );
    assert!(!ticket.is_closed);
}

#[tokio::test]
async fn test_desktop_portable_operator_mnemonic_lifecycle() {
    let state = CanonicalDesktopState::new_default();

    assert!(
        !state.is_operator_unlocked().await,
        "Initially operator session must be locked"
    );

    let mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art";
    let session = state
        .unlock_operator(mnemonic)
        .await
        .expect("Mnemonic unlock must succeed");

    assert_eq!(session.operator_entity.len(), 64);
    assert!(session.capabilities.contains(&"admin".to_string()));
    assert!(session.capabilities.contains(&"shell".to_string()));
    assert!(session.expires_at > session.issued_at);
    assert!(
        state.is_operator_unlocked().await,
        "Operator workspace must now be unlocked"
    );

    // Lock and verify memory wipe
    state.lock_operator().await;
    assert!(
        !state.is_operator_unlocked().await,
        "Operator workspace must be wiped and locked"
    );
}

#[tokio::test]
async fn test_desktop_terminal_state_revocation() {
    let state = fortiq_desktop_lib::TerminalState(tokio::sync::Mutex::new(None));
    // When no session is active, revocation is a clean no-op
    state.revoke_session().await;
    let guard = state.0.lock().await;
    assert!(guard.is_none());
}
