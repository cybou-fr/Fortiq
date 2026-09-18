//! Canonical v4 security smoke tests.
//!
//! These tests cover the current lifecycle-only ticket authority model. The old
//! v3 AccessEpoch suite is superseded and must not silently define authority.

use fortiq_core::canonical::portable::certificate::OperatorCapabilities;
use fortiq_core::{TicketDb, TicketPriority, TicketState};

#[test]
fn ticket_lifecycle_is_the_only_ticket_level_shell_gate() {
    let db = TicketDb::open_in_memory().unwrap();
    let ticket = db
        .create_ticket(
            "Security review",
            "Lifecycle authority",
            TicketPriority::High,
            "client-peer",
        )
        .unwrap();
    assert!(ticket.state.permits_work());

    let resolved = db
        .update_ticket_state(&ticket.id, TicketState::Resolved, "operator-session")
        .unwrap()
        .unwrap();
    assert!(!resolved.state.permits_work());

    let resumed = db
        .update_ticket_state(&ticket.id, TicketState::InProgress, "operator-session")
        .unwrap()
        .unwrap();
    assert!(resumed.state.permits_work());

    let closed = db
        .update_ticket_state(&ticket.id, TicketState::Closed, "operator-session")
        .unwrap()
        .unwrap();
    assert!(!closed.state.permits_work());
    assert!(db
        .update_ticket_state(&ticket.id, TicketState::InProgress, "operator-session")
        .is_err());
}

#[test]
fn stale_ticket_revision_is_rejected_even_with_different_history() {
    let db = TicketDb::open_in_memory().unwrap();
    let ticket = db
        .create_ticket(
            "Revision review",
            "Stale state must fail closed",
            TicketPriority::Normal,
            "client-peer",
        )
        .unwrap();
    let mut stale = ticket.clone();
    stale.revision = 0;
    assert!(db.import_canonical_ticket(&stale).is_err());
}

#[test]
fn ticket_manage_is_distinct_from_shell_execute() {
    let shell = OperatorCapabilities::from_bits(OperatorCapabilities::SHELL_EXEC);
    assert!(shell.has(OperatorCapabilities::SHELL_EXEC));
    assert!(!shell.has(OperatorCapabilities::TICKET_MANAGE));
}
