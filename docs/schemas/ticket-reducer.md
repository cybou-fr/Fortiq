# Ticket Reducer

Conceptual lifecycle state:

```rust
struct TicketSafetyState {
    ticket_id: TicketId,
    lifecycle: TicketLifecycle,
}

enum TicketLifecycle {
    Open,
    InProgress,
    Resolved,
    Closed,
}
```

Rules:

```text
TicketCreated(client)
  -> OPEN

TicketInProgress(operator)
  -> IN_PROGRESS

TicketResolved(operator)
  -> RESOLVED, terminate active shells

TicketClosed(operator|admin)
  -> CLOSED, terminate active shells

TicketResolved(operator)
  -> IN_PROGRESS is allowed when the operator resumes work

CLOSED
  -> terminal; create a new ticket for new work
```

Shell admission is exactly `lifecycle in {OPEN, IN_PROGRESS}` plus a valid
Owner-signed Operator Session. `TicketCryptoEpoch` remains an encryption-key
rotation mechanism and has no relationship to shell authorization.
